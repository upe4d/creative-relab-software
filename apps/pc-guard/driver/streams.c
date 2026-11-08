#include "guard.h"
/* Контекст потока связывает все дескрипторы и жёсткие ссылки с одним File ID. */
BOOLEAN PcgNameWithin(PCUNICODE_STRING name,const PCG_PATH* root){UNICODE_STRING r;SIZE_T length=0;while(length<PCG_PATH_CHARS&&root->Text[length])++length;if(length==PCG_PATH_CHARS)return FALSE;r.Buffer=(PWCH)root->Text;r.Length=(USHORT)(length*2);r.MaximumLength=r.Length;return RtlPrefixUnicodeString(&r,name,TRUE)&&(name->Length==r.Length||(name->Length>r.Length&&name->Buffer[length]==L'\\'));}
static BOOLEAN Names(const PCG_HEADER* policy,PCUNICODE_STRING name,BOOLEAN exec,BOOLEAN ancestor){ULONG i,count;const PCG_PATH* roots;if(!policy)return FALSE;if(exec){if(!(policy->Flags&PCG_FLAG_EXEC))return FALSE;roots=PcgExec(policy);count=policy->ExecCount;}else{if(!(policy->Flags&PCG_FLAG_WRITE))return FALSE;roots=PcgProtected(policy);count=policy->ProtectCount;}for(i=0;i<count;++i){if(PcgNameWithin(name,roots+i))return TRUE;if(ancestor){UNICODE_STRING r;SIZE_T n=0;while(n<PCG_PATH_CHARS&&roots[i].Text[n])++n;r.Buffer=(PWCH)roots[i].Text;r.Length=(USHORT)(n*2);r.MaximumLength=r.Length;if(RtlPrefixUnicodeString(name,&r,TRUE)&&(name->Length==r.Length||(name->Length<r.Length&&r.Buffer[name->Length/2]==L'\\')))return TRUE;}}return FALSE;}
static BOOLEAN Volume(const PCG_HEADER* policy,PCUNICODE_STRING name){ULONG i,j;if(!policy||!(policy->Flags&PCG_FLAG_WRITE))return FALSE;for(i=0;i<policy->ProtectCount;++i){const PCG_PATH* root=PcgProtected(policy)+i;for(j=8;j<PCG_PATH_CHARS&&root->Text[j]&&root->Text[j]!=L'\\';++j){}if(j*2<=name->Length){UNICODE_STRING v;v.Buffer=(PWCH)root->Text;v.Length=(USHORT)(j*2);v.MaximumLength=v.Length;if(RtlPrefixUnicodeString(&v,name,TRUE)&&(j*2==name->Length||name->Buffer[j]==L'\\'))return TRUE;}}return FALSE;}
BOOLEAN PcgProtectedVolume(PCUNICODE_STRING name){BOOLEAN result;PcgReadLock();result=Volume(PcgPolicy,name)||Volume(PcgPending,name);PcgReadUnlock();return result;}
BOOLEAN PcgProtectedName(PCUNICODE_STRING name){BOOLEAN result;PcgReadLock();result=Names(PcgPolicy,name,FALSE,FALSE);PcgReadUnlock();return result;}
BOOLEAN PcgProtectedAncestor(PCUNICODE_STRING name){BOOLEAN result;PcgReadLock();result=Names(PcgPolicy,name,FALSE,TRUE);PcgReadUnlock();return result;}
BOOLEAN PcgPendingName(PCUNICODE_STRING name,BOOLEAN ancestors){BOOLEAN result;PcgReadLock();result=Names(PcgPending,name,FALSE,ancestors);PcgReadUnlock();return result;}
BOOLEAN PcgExecutionName(PCUNICODE_STRING name){BOOLEAN result;PcgReadLock();result=Names(PcgPolicy,name,TRUE,FALSE);PcgReadUnlock();return result;}
NTSTATUS PcgGetStream(PCFLT_RELATED_OBJECTS objects,PCG_STREAM** result){return FltGetStreamContext(objects->Instance,objects->FileObject,(PFLT_CONTEXT*)result);}
VOID PcgInvalidateStream(PCG_STREAM* stream){KIRQL irql;KeAcquireSpinLock(&stream->Lock,&irql);stream->HashValid=FALSE;++stream->Epoch;KeReleaseSpinLock(&stream->Lock,irql);}
BOOLEAN PcgStreamProtected(PCG_STREAM* stream){BOOLEAN byName=FALSE;if(stream->Name.Text[0]){UNICODE_STRING name;RtlInitUnicodeString(&name,(PCWSTR)stream->Name.Text);byName=PcgProtectedName(&name);}return byName||(stream->HasId&&PcgProtectedId(&stream->Id));}
FLT_POSTOP_CALLBACK_STATUS PcgPostCreate(PFLT_CALLBACK_DATA data,PCFLT_RELATED_OBJECTS objects,PVOID context,FLT_POST_OPERATION_FLAGS flags){
    PCG_STREAM *stream=NULL,*existing=NULL;PFLT_FILE_NAME_INFORMATION name=NULL;NTSTATUS status;BOOLEAN protectedFile=FALSE,watch=FALSE,write,allow,active,executionDenied=FALSE;ULONG_PTR information=data->IoStatus.Information;
    UNREFERENCED_PARAMETER(context);if(FlagOn(flags,FLTFL_POST_OPERATION_DRAINING)||!NT_SUCCESS(data->IoStatus.Status)||data->IoStatus.Status==STATUS_REPARSE)return FLT_POSTOP_FINISHED_PROCESSING;
    PcgReadLock();active=(PcgPolicy&&(PcgPolicy->Flags&3))||PcgPending!=NULL;PcgReadUnlock();if(!active)return FLT_POSTOP_FINISHED_PROCESSING;
    write=objects->FileObject->WriteAccess||objects->FileObject->DeleteAccess;
    status=FltGetFileNameInformation(data,FLT_FILE_NAME_NORMALIZED|FLT_FILE_NAME_QUERY_DEFAULT,&name);
    if(NT_SUCCESS(status)){protectedFile=PcgProtectedName(&name->Name);watch=PcgExecutionName(&name->Name);if(write&&PcgPendingName(&name->Name,FALSE))goto deny;}
    if(NT_SUCCESS(FltGetStreamContext(objects->Instance,objects->FileObject,(PFLT_CONTEXT*)&existing)))stream=existing;
    else{
        status=FltAllocateContext(PcgFilter,FLT_STREAM_CONTEXT,sizeof(PCG_STREAM),NonPagedPoolNx,(PFLT_CONTEXT*)&stream);if(!NT_SUCCESS(status))goto unable;
        RtlZeroMemory(stream,sizeof(*stream));KeInitializeSpinLock(&stream->Lock);
        if(name&&name->Name.Length<sizeof(stream->Name.Text))RtlCopyMemory(stream->Name.Text,name->Name.Buffer,name->Name.Length);
        status=PcgQueryId(objects->Instance,objects->FileObject,&stream->Id);stream->HasId=NT_SUCCESS(status);
        status=FltSetStreamContext(objects->Instance,objects->FileObject,FLT_SET_CONTEXT_KEEP_IF_EXISTS,stream,(PFLT_CONTEXT*)&existing);
        if(status==STATUS_FLT_CONTEXT_ALREADY_DEFINED){FltReleaseContext(stream);stream=existing;existing=NULL;}else if(!NT_SUCCESS(status))goto unable;
    }
    if(write)PcgInvalidateStream(stream);
    protectedFile=protectedFile||PcgStreamProtected(stream);allow=PcgPermittedProcess(PsGetCurrentProcess());
    if(data->RequestorMode==UserMode&&write&&protectedFile&&!allow)goto deny;
    /* Фиксируем хеш только при отсутствии пишущих дескрипторов/отображений. Запись инвалидирует его. */
    if(watch&&(data->Iopb->Parameters.Create.SecurityContext->DesiredAccess&FILE_EXECUTE)&&objects->FileObject->ReadAccess&&!objects->FileObject->WriteAccess&&!objects->FileObject->SharedWrite&&(!objects->FileObject->SectionObjectPointer||!MmDoesFileHaveUserWritableReferences(objects->FileObject->SectionObjectPointer))){
        PCG_U8 hash[32];LONG epoch;KIRQL irql;KeAcquireSpinLock(&stream->Lock,&irql);epoch=stream->Epoch;KeReleaseSpinLock(&stream->Lock,irql);
        status=PcgHashFile(objects->FileObject,hash);if(NT_SUCCESS(status)){KeAcquireSpinLock(&stream->Lock,&irql);if(epoch==stream->Epoch){RtlCopyMemory(stream->Hash,hash,32);stream->HashValid=TRUE;}else status=STATUS_RETRY;KeReleaseSpinLock(&stream->Lock,irql);}
        PcgReadLock();executionDenied=PcgPolicy&&((NT_SUCCESS(status)&&PcgBlocked(PcgPolicy,hash))||(!NT_SUCCESS(status)&&(PcgPolicy->Flags&PCG_FLAG_FAIL_CLOSED)));PcgReadUnlock();
        if(!NT_SUCCESS(status))InterlockedIncrement(&PcgErrors);if(executionDenied)goto deny;
    }
    goto done;
unable:
    if(data->RequestorMode==UserMode&&write&&protectedFile){InterlockedIncrement(&PcgErrors);goto deny;}goto done;
deny:
    if(data->RequestorMode==UserMode){FltCancelFileOpen(objects->Instance,objects->FileObject);data->IoStatus.Status=STATUS_ACCESS_DENIED;data->IoStatus.Information=0;if(executionDenied)InterlockedIncrement(&PcgStarts);else InterlockedIncrement(&PcgWrites);}else data->IoStatus.Information=information;
done:if(stream)FltReleaseContext(stream);if(name)FltReleaseFileNameInformation(name);return FLT_POSTOP_FINISHED_PROCESSING;
}
/* Контракт WDK не предоставляет здесь общего ACCESS_DENIED. Только инвалидируем кеш. */
FLT_PREOP_CALLBACK_STATUS PcgPreSection(PFLT_CALLBACK_DATA data,PCFLT_RELATED_OBJECTS objects,PVOID* context){
    PCG_STREAM* stream=NULL;ULONG page=data->Iopb->Parameters.AcquireForSectionSynchronization.PageProtection;UNREFERENCED_PARAMETER(context);
    if(data->Iopb->Parameters.AcquireForSectionSynchronization.SyncType==SyncTypeCreateSection&&((page&PAGE_READWRITE)||(page&PAGE_EXECUTE_READWRITE))&&NT_SUCCESS(PcgGetStream(objects,&stream))){PcgInvalidateStream(stream);FltReleaseContext(stream);}
    return FLT_PREOP_SUCCESS_NO_CALLBACK;
}
