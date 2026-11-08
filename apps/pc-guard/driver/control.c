#include "guard.h"
PCG_HEADER* PcgPending;
PCG_ID* PcgIds;
ULONG PcgIdCount;
static KMUTEX ControlGate;
static PFILE_OBJECT PendingOwner;
static ULONG PendingBytes;
VOID PcgInitializeControl(VOID){KeInitializeMutex(&ControlGate,0);}
static VOID Abort(VOID){PCG_HEADER* old;KeEnterCriticalRegion();ExAcquirePushLockExclusive(&PcgLock);old=PcgPending;PcgPending=NULL;PendingOwner=NULL;PendingBytes=0;ExReleasePushLockExclusive(&PcgLock);KeLeaveCriticalRegion();if(old)ExFreePoolWithTag(old,PCG_TAG);}
VOID PcgAbortOwner(PFILE_OBJECT owner){KeWaitForSingleObject(&ControlGate,Executive,KernelMode,FALSE,NULL);if(PendingOwner==owner)Abort();KeReleaseMutex(&ControlGate,FALSE);}
VOID PcgFreeControl(VOID){Abort();if(PcgIds){ExFreePoolWithTag(PcgIds,PCG_TAG);PcgIds=NULL;}PcgIdCount=0;}
NTSTATUS PcgQueryId(PFLT_INSTANCE instance,PFILE_OBJECT file,PCG_ID* id){FILE_ID_INFORMATION info;NTSTATUS status;ULONG i;BOOLEAN nonzero=FALSE;
    if(KeGetCurrentIrql()!=PASSIVE_LEVEL||KeAreAllApcsDisabled()||IoGetTopLevelIrp()!=NULL)return STATUS_CANT_WAIT;
    status=FltQueryInformationFile(instance,file,&info,sizeof(info),FileIdInformation,NULL);if(!NT_SUCCESS(status))return status;
    for(i=0;i<16;++i)if(info.FileId.Identifier[i])nonzero=TRUE;if(!nonzero||!info.VolumeSerialNumber)return STATUS_NOT_SUPPORTED;
    id->Volume=info.VolumeSerialNumber;RtlCopyMemory(id->Id,info.FileId.Identifier,16);return STATUS_SUCCESS;
}
BOOLEAN PcgProtectedId(const PCG_ID* id){BOOLEAN found;PcgReadLock();found=PcgIdContains(PcgIds,PcgIdCount,id)!=0;PcgReadUnlock();return found;}
static NTSTATUS Probe(PCG_U64 handle,PCG_ID* id){PFILE_OBJECT file=NULL;PFLT_VOLUME volume=NULL;PFLT_INSTANCE instance=NULL;PFLT_FILE_NAME_INFORMATION name=NULL;NTSTATUS status;PCG_PATH path;ULONG i;BOOLEAN belongs=FALSE;
    status=ObReferenceObjectByHandle((HANDLE)(ULONG_PTR)handle,FILE_READ_DATA,*IoFileObjectType,UserMode,(PVOID*)&file,NULL);if(!NT_SUCCESS(status))return status;
    if(file->WriteAccess||file->SharedWrite||file->SharedDelete||!file->ReadAccess){status=STATUS_SHARING_VIOLATION;goto done;}
    if(file->SectionObjectPointer&&MmDoesFileHaveUserWritableReferences(file->SectionObjectPointer)){status=STATUS_USER_MAPPED_FILE;goto done;}
    status=FltGetVolumeFromFileObject(PcgFilter,file,&volume);if(!NT_SUCCESS(status))goto done;
    status=FltGetVolumeInstanceFromName(PcgFilter,volume,NULL,&instance);if(!NT_SUCCESS(status))goto done;
    status=FltGetFileNameInformationUnsafe(file,instance,FLT_FILE_NAME_NORMALIZED|FLT_FILE_NAME_QUERY_DEFAULT,&name);if(!NT_SUCCESS(status))goto done;
    if(name->Name.Length>=sizeof(path.Text)){status=STATUS_NAME_TOO_LONG;goto done;}RtlZeroMemory(&path,sizeof(path));RtlCopyMemory(path.Text,name->Name.Buffer,name->Name.Length);
    for(i=0;i<PcgPending->ProtectCount;++i)if(PcgWithin(path.Text,PcgProtected(PcgPending)+i)){belongs=TRUE;break;}
    if(!belongs){status=STATUS_ACCESS_DENIED;goto done;}status=PcgQueryId(instance,file,id);
done:if(name)FltReleaseFileNameInformation(name);if(instance)FltObjectDereference(instance);if(volume)FltObjectDereference(volume);if(file)ObDereferenceObject(file);return status;
}
static NTSTATUS Command(PIO_STACK_LOCATION stack,PIRP irp,ULONG_PTR* bytes){
    ULONG code=stack->Parameters.DeviceIoControl.IoControlCode,size=stack->Parameters.DeviceIoControl.InputBufferLength,i,j,count=0,metadata;
    PCG_HEADER *input=(PCG_HEADER*)irp->AssociatedIrp.SystemBuffer,*next=NULL,*old;PCG_ID* ids=NULL,*oldIds;PCG_STATUS result;NTSTATUS status;
    if(code==PCG_IOCTL_STATUS){if(stack->Parameters.DeviceIoControl.OutputBufferLength<sizeof(result))return STATUS_BUFFER_TOO_SMALL;RtlZeroMemory(&result,sizeof(result));result.Version=PCG_VERSION;
        PcgReadLock();if(PcgPolicy){result.Flags=PcgPolicy->Flags;result.HashCount=PcgPolicy->HashCount;result.ExecCount=PcgPolicy->ExecCount;result.ProtectCount=PcgPolicy->ProtectCount;result.PermitCount=PcgPolicy->PermitCount;}result.Generation=(ULONG)PcgGeneration;result.Preparing=PcgPending!=NULL;result.ProtectedIds=PcgIdCount;PcgReadUnlock();result.BlockedStarts=(ULONG)PcgStarts;result.BlockedWrites=(ULONG)PcgWrites;result.ScanErrors=(ULONG)PcgErrors;RtlCopyMemory(input,&result,sizeof(result));*bytes=sizeof(result);return STATUS_SUCCESS;}
    if(code!=PCG_IOCTL_CONFIG&&code!=PCG_IOCTL_COMMIT&&code!=PCG_IOCTL_ABORT&&code!=PCG_IOCTL_BROKER)return STATUS_INVALID_DEVICE_REQUEST;
    KeWaitForSingleObject(&ControlGate,Executive,KernelMode,FALSE,NULL);
    if(code==PCG_IOCTL_BROKER){if(size){status=STATUS_INVALID_PARAMETER;goto done;}PcgReadLock();status=PcgPolicy&&PcgPolicy->PermitCount?STATUS_SUCCESS:STATUS_INVALID_DEVICE_STATE;PcgReadUnlock();if(NT_SUCCESS(status))PcgSetController(PsGetCurrentProcess());goto done;}
    if(code==PCG_IOCTL_ABORT){status=PendingOwner==stack->FileObject?STATUS_SUCCESS:STATUS_ACCESS_DENIED;if(NT_SUCCESS(status))Abort();goto done;}
    if(!PcgValidate(input,size)){status=STATUS_INVALID_PARAMETER;goto done;}
    if(code==PCG_IOCTL_CONFIG){
        if(PcgPending){status=STATUS_DEVICE_BUSY;goto done;}if(input->ProbeCount){status=STATUS_INVALID_PARAMETER;goto done;}
        next=ExAllocatePool2(POOL_FLAG_NON_PAGED,size,PCG_TAG);if(!next){status=STATUS_INSUFFICIENT_RESOURCES;goto done;}RtlCopyMemory(next,input,size);
        KeEnterCriticalRegion();ExAcquirePushLockExclusive(&PcgLock);PcgPending=next;PendingOwner=stack->FileObject;PendingBytes=size;ExReleasePushLockExclusive(&PcgLock);KeLeaveCriticalRegion();next=NULL;status=STATUS_SUCCESS;goto done;
    }
    if(PendingOwner!=stack->FileObject||!PcgPending){status=STATUS_ACCESS_DENIED;goto done;}
    metadata=size-input->ProbeCount*(ULONG)sizeof(PCG_PROBE);
    {PCG_HEADER header=*input;header.ProbeCount=0;if(metadata!=PendingBytes||RtlCompareMemory(&header,PcgPending,sizeof(header))!=sizeof(header)||RtlCompareMemory(input+1,PcgPending+1,metadata-sizeof(header))!=metadata-sizeof(header)){status=STATUS_REVISION_MISMATCH;goto done;}}
    if(input->ProbeCount&&!input->ProtectCount){status=STATUS_INVALID_PARAMETER;goto done;}
    if(input->ProbeCount){ids=ExAllocatePool2(POOL_FLAG_NON_PAGED,input->ProbeCount*sizeof(PCG_ID),PCG_TAG);if(!ids){status=STATUS_INSUFFICIENT_RESOURCES;goto done;}}
    for(i=0;i<input->ProbeCount;++i){status=Probe(PcgProbes(input)[i].Handle,ids+i);if(!NT_SUCCESS(status))goto done;}
    /* 2000 записей: ограниченная сортировка; точные ID закрывают доступ через старые жёсткие ссылки. */
    for(i=1;i<input->ProbeCount;++i){PCG_ID value=ids[i];j=i;while(j&&PcgIdCompare(ids+j-1,&value)>0){ids[j]=ids[j-1];--j;}ids[j]=value;}
    for(i=0;i<input->ProbeCount;++i)if(!count||PcgIdCompare(ids+i,ids+count-1)!=0)ids[count++]=ids[i];
    next=ExAllocatePool2(POOL_FLAG_NON_PAGED,metadata,PCG_TAG);if(!next){status=STATUS_INSUFFICIENT_RESOURCES;goto done;}RtlCopyMemory(next,PcgPending,metadata);
    KeEnterCriticalRegion();ExAcquirePushLockExclusive(&PcgLock);old=PcgPolicy;oldIds=PcgIds;PcgPolicy=next;PcgIds=ids;PcgIdCount=count;InterlockedIncrement(&PcgGeneration);ExReleasePushLockExclusive(&PcgLock);KeLeaveCriticalRegion();next=NULL;ids=NULL;if(old)ExFreePoolWithTag(old,PCG_TAG);if(oldIds)ExFreePoolWithTag(oldIds,PCG_TAG);Abort();status=STATUS_SUCCESS;
done:if(next)ExFreePoolWithTag(next,PCG_TAG);if(ids)ExFreePoolWithTag(ids,PCG_TAG);KeReleaseMutex(&ControlGate,FALSE);return status;
}
NTSTATUS PcgControl(PDEVICE_OBJECT device,PIRP irp){ULONG_PTR bytes=0;NTSTATUS status;UNREFERENCED_PARAMETER(device);if(KeGetCurrentIrql()!=PASSIVE_LEVEL)status=STATUS_INVALID_DEVICE_STATE;else status=Command(IoGetCurrentIrpStackLocation(irp),irp,&bytes);irp->IoStatus.Status=status;irp->IoStatus.Information=bytes;IoCompleteRequest(irp,IO_NO_INCREMENT);return status;}
