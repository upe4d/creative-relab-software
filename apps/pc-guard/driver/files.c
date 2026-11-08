#include "guard.h"
/* Все решения ограничены выбранными корнями; повторное открытие ниже фильтра нужно для старых жёстких ссылок. */
static BOOLEAN Mutating(PFLT_CALLBACK_DATA data){
    if(data->Iopb->MajorFunction==IRP_MJ_WRITE)return TRUE;
    if(data->Iopb->MajorFunction==IRP_MJ_CREATE){ACCESS_MASK access=data->Iopb->Parameters.Create.SecurityContext->DesiredAccess;ULONG options=data->Iopb->Parameters.Create.Options;return (access&(FILE_WRITE_DATA|FILE_APPEND_DATA|FILE_WRITE_ATTRIBUTES|FILE_WRITE_EA|DELETE|WRITE_DAC|WRITE_OWNER))!=0||(options&FILE_DELETE_ON_CLOSE)!=0||(options>>24)==FILE_SUPERSEDE||(options>>24)==FILE_OVERWRITE||(options>>24)==FILE_OVERWRITE_IF;}
    if(data->Iopb->MajorFunction==IRP_MJ_FILE_SYSTEM_CONTROL){ULONG code=data->Iopb->Parameters.FileSystemControl.Common.FsControlCode;return code==FSCTL_SET_REPARSE_POINT||code==FSCTL_DELETE_REPARSE_POINT||code==FSCTL_SET_ZERO_DATA||code==FSCTL_SET_SPARSE||code==FSCTL_SET_COMPRESSION||code==FSCTL_SET_ENCRYPTION||code==FSCTL_DUPLICATE_EXTENTS_TO_FILE||code==FSCTL_OFFLOAD_WRITE;}
    if(data->Iopb->MajorFunction==IRP_MJ_SET_INFORMATION){FILE_INFORMATION_CLASS kind=data->Iopb->Parameters.SetFileInformation.FileInformationClass;return kind==FileRenameInformation||kind==FileRenameInformationEx||kind==FileLinkInformation||kind==FileLinkInformationEx||kind==FileDispositionInformation||kind==FileDispositionInformationEx||kind==FileEndOfFileInformation||kind==FileAllocationInformation||kind==FileValidDataLengthInformation||kind==FileBasicInformation;}
    return FALSE;
}
static BOOLEAN Directory(PCFLT_RELATED_OBJECTS objects){BOOLEAN directory=TRUE;if(!NT_SUCCESS(FltIsDirectory(objects->FileObject,objects->Instance,&directory)))return TRUE;return directory;}
static NTSTATUS ExistingId(PCFLT_RELATED_OBJECTS objects,PUNICODE_STRING name,BOOLEAN* protectedFile,BOOLEAN* directory){
    OBJECT_ATTRIBUTES attributes;IO_STATUS_BLOCK io;HANDLE handle=NULL;PFILE_OBJECT file=NULL;PCG_ID id;NTSTATUS status;UNICODE_STRING base=*name;USHORT i;PCG_STREAM* stream=NULL;
    if(KeGetCurrentIrql()!=PASSIVE_LEVEL||KeAreAllApcsDisabled()||IoGetTopLevelIrp()!=NULL)return STATUS_CANT_WAIT;
    for(i=0;i<base.Length/2;++i)if(base.Buffer[i]==L':'){base.Length=(USHORT)(i*2);break;}
    InitializeObjectAttributes(&attributes,&base,OBJ_CASE_INSENSITIVE|OBJ_KERNEL_HANDLE,NULL,NULL);
    status=FltCreateFileEx2(PcgFilter,objects->Instance,&handle,&file,FILE_READ_ATTRIBUTES|SYNCHRONIZE,&attributes,&io,NULL,0,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,FILE_OPEN,FILE_SYNCHRONOUS_IO_NONALERT|FILE_OPEN_REPARSE_POINT,NULL,0,0,NULL);
    if(status==STATUS_OBJECT_NAME_NOT_FOUND||status==STATUS_OBJECT_PATH_NOT_FOUND)return STATUS_SUCCESS;
    if(NT_SUCCESS(status)){if(NT_SUCCESS(FltGetStreamContext(objects->Instance,file,(PFLT_CONTEXT*)&stream))){PcgInvalidateStream(stream);FltReleaseContext(stream);}status=PcgQueryId(objects->Instance,file,&id);if(NT_SUCCESS(status)){*protectedFile=*protectedFile||PcgProtectedId(&id);status=FltIsDirectory(file,objects->Instance,directory);}}
    if(file)ObDereferenceObject(file);if(handle)FltClose(handle);return status;
}
FLT_PREOP_CALLBACK_STATUS PcgPreOperation(PFLT_CALLBACK_DATA data,PCFLT_RELATED_OBJECTS objects,PVOID* context){
    PFLT_FILE_NAME_INFORMATION name=NULL,destination=NULL;PCG_STREAM* stream=NULL;NTSTATUS status=STATUS_SUCCESS;BOOLEAN protectedFile=FALSE,knownId=FALSE,directory=FALSE,active,blocked=FALSE,permitted=FALSE,create=data->Iopb->MajorFunction==IRP_MJ_CREATE;PEPROCESS process;
    UNREFERENCED_PARAMETER(context);
    if(!create&&NT_SUCCESS(PcgGetStream(objects,&stream))){if(Mutating(data))PcgInvalidateStream(stream);protectedFile=PcgStreamProtected(stream);knownId=stream->HasId;FltReleaseContext(stream);}
    PcgReadLock();active=(PcgPolicy&&(PcgPolicy->Flags&3))||(PcgPending&&(PcgPending->Flags&PCG_FLAG_WRITE));PcgReadUnlock();
    if(!active||!Mutating(data)||data->RequestorMode==KernelMode||FlagOn(data->Iopb->IrpFlags,IRP_PAGING_IO))return create?FLT_PREOP_SUCCESS_WITH_CALLBACK:FLT_PREOP_SUCCESS_NO_CALLBACK;
    /* Открытие по ID не даёт надёжного пути до создания FILE_OBJECT. В режиме защиты такое изменение запрещено. */
    if(create&&FlagOn(data->Iopb->Parameters.Create.Options,FILE_OPEN_BY_FILE_ID)){blocked=TRUE;goto done;}
    process=FltGetRequestorProcess(data);if(process)permitted=PcgPermittedProcess(process);
    status=FltGetFileNameInformation(data,FLT_FILE_NAME_NORMALIZED|FLT_FILE_NAME_QUERY_DEFAULT,&name);if(!NT_SUCCESS(status)){blocked=TRUE;goto done;}
    if(!PcgProtectedVolume(&name->Name)&&!PcgExecutionName(&name->Name))goto done;
    protectedFile=protectedFile||PcgProtectedName(&name->Name);
    if(PcgPendingName(&name->Name,FALSE)){blocked=TRUE;goto done;}
    if(create){status=ExistingId(objects,&name->Name,&protectedFile,&directory);if(!NT_SUCCESS(status)){blocked=TRUE;goto done;}
        if(directory&&(PcgProtectedAncestor(&name->Name)||PcgPendingName(&name->Name,TRUE))&&((data->Iopb->Parameters.Create.SecurityContext->DesiredAccess&DELETE)||FlagOn(data->Iopb->Parameters.Create.Options,FILE_DELETE_ON_CLOSE))){blocked=TRUE;goto done;}}
    if(!create&&!protectedFile&&!knownId){PCG_ID id;status=PcgQueryId(objects->Instance,objects->FileObject,&id);if(NT_SUCCESS(status))protectedFile=PcgProtectedId(&id);else{blocked=TRUE;goto done;}}
    if(data->Iopb->MajorFunction==IRP_MJ_FILE_SYSTEM_CONTROL&&(PcgProtectedAncestor(&name->Name)||PcgPendingName(&name->Name,TRUE))){blocked=TRUE;goto done;}
    if(data->Iopb->MajorFunction==IRP_MJ_SET_INFORMATION){
        FILE_INFORMATION_CLASS kind=data->Iopb->Parameters.SetFileInformation.FileInformationClass;
        BOOLEAN link=kind==FileLinkInformation||kind==FileLinkInformationEx;
        BOOLEAN rename=kind==FileRenameInformation||kind==FileRenameInformationEx;
        if((rename||link)&&PcgPendingName(&name->Name,TRUE)){blocked=TRUE;goto done;}
        if(rename||link){
            PFILE_RENAME_INFORMATION target=(PFILE_RENAME_INFORMATION)data->Iopb->Parameters.SetFileInformation.InfoBuffer;ULONG length=data->Iopb->Parameters.SetFileInformation.Length;BOOLEAN targetProtected;
            if(!target||length<(ULONG)FIELD_OFFSET(FILE_RENAME_INFORMATION,FileName)||!target->FileNameLength||(target->FileNameLength&1)||target->FileNameLength>length-FIELD_OFFSET(FILE_RENAME_INFORMATION,FileName)){status=STATUS_INVALID_PARAMETER;blocked=TRUE;goto done;}
            status=FltGetDestinationFileNameInformation(objects->Instance,objects->FileObject,target->RootDirectory,target->FileName,target->FileNameLength,FLT_FILE_NAME_NORMALIZED|FLT_FILE_NAME_QUERY_DEFAULT,&destination);if(!NT_SUCCESS(status)){blocked=TRUE;goto done;}
            targetProtected=PcgProtectedName(&destination->Name);
            if(PcgPendingName(&destination->Name,TRUE)||(link&&(protectedFile||targetProtected))||(protectedFile&&!targetProtected)||(rename&&PcgProtectedAncestor(&name->Name)&&Directory(objects))){blocked=TRUE;goto done;}
            protectedFile=protectedFile||targetProtected;
        }else if((kind==FileDispositionInformation||kind==FileDispositionInformationEx)&&PcgProtectedAncestor(&name->Name)&&Directory(objects)){blocked=TRUE;goto done;}
    }
    blocked=protectedFile&&!permitted;
done:
    if(name)FltReleaseFileNameInformation(name);if(destination)FltReleaseFileNameInformation(destination);
    if(!blocked)return create?FLT_PREOP_SUCCESS_WITH_CALLBACK:FLT_PREOP_SUCCESS_NO_CALLBACK;
    if(!NT_SUCCESS(status))InterlockedIncrement(&PcgErrors);data->IoStatus.Status=STATUS_ACCESS_DENIED;data->IoStatus.Information=0;InterlockedIncrement(&PcgWrites);return FLT_PREOP_COMPLETE;
}
