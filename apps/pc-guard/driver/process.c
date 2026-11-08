#include "guard.h"
/* Хеш считается по FILE_OBJECT самого запускаемого образа, а не по повторно открытому пути. */
typedef struct {PEPROCESS Process;LONG Generation;} PCG_VERIFIED;
static PCG_VERIFIED Verified[1024];
static EX_PUSH_LOCK ProcessLock;
static PEPROCESS Controller;
static volatile LONG ActiveHashes;
BOOLEAN PcgPermittedProcess(PEPROCESS process){ULONG i;BOOLEAN allowed=FALSE;KeEnterCriticalRegion();ExAcquirePushLockShared(&ProcessLock);for(i=0;i<RTL_NUMBER_OF(Verified);++i)if(Verified[i].Process==process&&Verified[i].Generation==PcgGeneration){allowed=TRUE;break;}ExReleasePushLockShared(&ProcessLock);KeLeaveCriticalRegion();return allowed;}
BOOLEAN PcgControllerProcess(PEPROCESS process){BOOLEAN same;KeEnterCriticalRegion();ExAcquirePushLockShared(&ProcessLock);same=Controller&&Controller==process;ExReleasePushLockShared(&ProcessLock);KeLeaveCriticalRegion();return same;}
VOID PcgSetController(PEPROCESS process){PEPROCESS old;if(process)ObReferenceObject(process);KeEnterCriticalRegion();ExAcquirePushLockExclusive(&ProcessLock);old=Controller;Controller=process;ExReleasePushLockExclusive(&ProcessLock);KeLeaveCriticalRegion();if(old)ObDereferenceObject(old);}
BOOLEAN PcgTrustedCreator(PEPROCESS process){
    PACCESS_TOKEN token;PTOKEN_USER user=NULL;NTSTATUS status;BOOLEAN system=FALSE;
    if(PcgControllerProcess(process)||PcgPermittedProcess(process))return TRUE;
    token=PsReferencePrimaryToken(process);status=SeQueryInformationToken(token,TokenUser,(PVOID*)&user);PsDereferencePrimaryToken(token);
    if(NT_SUCCESS(status)&&user)system=RtlEqualSid(user->User.Sid,SeExports->SeLocalSystemSid);if(user)ExFreePool(user);return system;
}
static VOID Remember(PEPROCESS process,BOOLEAN add,LONG generation){ULONG i;PEPROCESS old=NULL;KeEnterCriticalRegion();ExAcquirePushLockExclusive(&ProcessLock);for(i=0;i<RTL_NUMBER_OF(Verified);++i)if(Verified[i].Process==process||(!Verified[i].Process&&add)){Verified[i].Process=add?process:NULL;Verified[i].Generation=generation;break;}if(!add&&Controller==process){old=Controller;Controller=NULL;}ExReleasePushLockExclusive(&ProcessLock);KeLeaveCriticalRegion();if(old)ObDereferenceObject(old);}
VOID PcgFreeProcesses(VOID){PcgSetController(NULL);RtlZeroMemory(Verified,sizeof(Verified));}
NTSTATUS PcgHashFile(PFILE_OBJECT file,PCG_U8 output[32]){
    PFLT_VOLUME volume=NULL;PFLT_INSTANCE instance=NULL;BCRYPT_ALG_HANDLE alg=NULL;BCRYPT_HASH_HANDLE hash=NULL;
    FILE_STANDARD_INFORMATION info;ULONG returned=0,got;LARGE_INTEGER offset;NTSTATUS status;PUCHAR buffer=NULL;ULONGLONG deadline=KeQueryInterruptTime()+30000000ULL;
    if(KeGetCurrentIrql()!=PASSIVE_LEVEL||KeAreAllApcsDisabled()||IoGetTopLevelIrp()!=NULL)return STATUS_CANT_WAIT;
    if(InterlockedIncrement(&ActiveHashes)>8){InterlockedDecrement(&ActiveHashes);return STATUS_DEVICE_BUSY;}
    status=FltGetVolumeFromFileObject(PcgFilter,file,&volume);if(!NT_SUCCESS(status))goto done;
    status=FltGetVolumeInstanceFromName(PcgFilter,volume,NULL,&instance);if(!NT_SUCCESS(status))goto done;
    status=FltQueryInformationFile(instance,file,&info,sizeof(info),FileStandardInformation,&returned);if(!NT_SUCCESS(status))goto done;
    if(info.Directory||info.EndOfFile.QuadPart<0||info.EndOfFile.QuadPart>64*1024*1024){status=STATUS_FILE_TOO_LARGE;goto done;}
    status=BCryptOpenAlgorithmProvider(&alg,BCRYPT_SHA256_ALGORITHM,NULL,0);if(!NT_SUCCESS(status))goto done;
    status=BCryptCreateHash(alg,&hash,NULL,0,NULL,0,0);if(!NT_SUCCESS(status))goto done;
    buffer=ExAllocatePool2(POOL_FLAG_NON_PAGED,65536,PCG_TAG);if(!buffer){status=STATUS_INSUFFICIENT_RESOURCES;goto done;}
    offset.QuadPart=0;
    while(offset.QuadPart<info.EndOfFile.QuadPart){
        ULONG want=(ULONG)min(65536,info.EndOfFile.QuadPart-offset.QuadPart);
        if(KeQueryInterruptTime()>deadline){status=STATUS_IO_TIMEOUT;goto done;}
        status=FltReadFile(instance,file,&offset,want,buffer,FLTFL_IO_OPERATION_DO_NOT_UPDATE_BYTE_OFFSET,&got,NULL,NULL);
        if(!NT_SUCCESS(status))goto done;if(got!=want){status=STATUS_END_OF_FILE;goto done;}
        status=BCryptHashData(hash,buffer,got,0);if(!NT_SUCCESS(status))goto done;offset.QuadPart+=got;
    }
    status=BCryptFinishHash(hash,output,32,0);
done:
    if(buffer)ExFreePoolWithTag(buffer,PCG_TAG);if(hash)BCryptDestroyHash(hash);if(alg)BCryptCloseAlgorithmProvider(alg,0);
    if(instance)FltObjectDereference(instance);if(volume)FltObjectDereference(volume);InterlockedDecrement(&ActiveHashes);return status;
}
/* Имя образа может быть длиннее поля корня политики: сравнивается полный UNICODE_STRING. */
static BOOLEAN SameName(PCUNICODE_STRING name,const PCG_PATH* path){UNICODE_STRING other;SIZE_T n=0;while(n<PCG_PATH_CHARS&&path->Text[n])++n;if(n==PCG_PATH_CHARS)return FALSE;other.Buffer=(PWCH)path->Text;other.Length=(USHORT)(n*2);other.MaximumLength=other.Length;return RtlEqualUnicodeString(name,&other,TRUE);}
/* При недоступном нормализованном имени fail-closed не должен превращаться в пропуск проверки. */
static VOID UnknownExecutable(PPS_CREATE_NOTIFY_INFO info){BOOLEAN closed;InterlockedIncrement(&PcgErrors);PcgReadLock();closed=PcgPolicy&&PcgPolicy->ExecCount&&(PcgPolicy->Flags&(PCG_FLAG_EXEC|PCG_FLAG_FAIL_CLOSED))==(PCG_FLAG_EXEC|PCG_FLAG_FAIL_CLOSED);PcgReadUnlock();if(closed){info->CreationStatus=STATUS_ACCESS_DENIED;InterlockedIncrement(&PcgStarts);}}
VOID PcgProcess(PEPROCESS process,HANDLE id,PPS_CREATE_NOTIFY_INFO info){
    PFLT_VOLUME volume=NULL;PFLT_INSTANCE instance=NULL;PFLT_FILE_NAME_INFORMATION name=NULL;PCG_U8 hash[32];ULONG i;BOOLEAN watch=FALSE,permit=FALSE,block=FALSE,closed=FALSE;NTSTATUS status;LONG generation;
    UNREFERENCED_PARAMETER(id);if(!info){Remember(process,FALSE,0);return;}
    if(!NT_SUCCESS(info->CreationStatus))return;
    PcgReadLock();if(!PcgPolicy||!(PcgPolicy->Flags&3)){PcgReadUnlock();return;}PcgReadUnlock();
    if(!info->FileObject){UnknownExecutable(info);goto done;}
    status=FltGetVolumeFromFileObject(PcgFilter,info->FileObject,&volume);if(!NT_SUCCESS(status)){UnknownExecutable(info);goto done;}
    status=FltGetVolumeInstanceFromName(PcgFilter,volume,NULL,&instance);if(!NT_SUCCESS(status)){UnknownExecutable(info);goto done;}
    status=FltGetFileNameInformationUnsafe(info->FileObject,instance,FLT_FILE_NAME_NORMALIZED|FLT_FILE_NAME_QUERY_DEFAULT,&name);if(!NT_SUCCESS(status)){UnknownExecutable(info);goto done;}
    PcgReadLock();generation=PcgGeneration;
    if(PcgPolicy){if(PcgPolicy->Flags&PCG_FLAG_EXEC)for(i=0;i<PcgPolicy->ExecCount;++i)if(PcgNameWithin(&name->Name,PcgExec(PcgPolicy)+i)){watch=TRUE;break;}
        for(i=0;i<PcgPolicy->PermitCount;++i)if(SameName(&name->Name,&PcgPermits(PcgPolicy)[i].Path)){permit=TRUE;break;}
        closed=(PcgPolicy->Flags&PCG_FLAG_FAIL_CLOSED)!=0;}
    PcgReadUnlock();if(!watch&&!permit)goto done;
    status=PcgHashFile(info->FileObject,hash);
    /* Ошибка хеширования не подтверждает право редактора, даже вне корней контроля запуска. */
    if(!NT_SUCCESS(status)){InterlockedIncrement(&PcgErrors);permit=FALSE;if(watch&&closed)block=TRUE;goto verdict;}
    PcgReadLock();
    if(generation!=PcgGeneration){block=watch;permit=FALSE;}
    else if(PcgPolicy){if(watch)block=PcgBlocked(PcgPolicy,hash)!=0;if(permit){permit=FALSE;for(i=0;i<PcgPolicy->PermitCount;++i)if(SameName(&name->Name,&PcgPermits(PcgPolicy)[i].Path)&&RtlCompareMemory(hash,PcgPermits(PcgPolicy)[i].Hash,32)==32){permit=TRUE;break;}}}
    PcgReadUnlock();
verdict:
    if(block){info->CreationStatus=STATUS_ACCESS_DENIED;InterlockedIncrement(&PcgStarts);}else if(permit&&PcgTrustedCreator(PsGetCurrentProcess()))Remember(process,TRUE,generation);
done:
    if(name)FltReleaseFileNameInformation(name);if(instance)FltObjectDereference(instance);if(volume)FltObjectDereference(volume);
}
