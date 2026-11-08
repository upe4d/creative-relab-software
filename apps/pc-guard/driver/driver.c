#include "guard.h"
PFLT_FILTER PcgFilter;
EX_PUSH_LOCK PcgLock;
PCG_HEADER* PcgPolicy;
volatile LONG PcgGeneration,PcgStarts,PcgWrites,PcgErrors;
static PDEVICE_OBJECT Device;
static BOOLEAN CallbackRegistered;
static LONG OpenHandles;
static BOOLEAN Stopping;
static FAST_MUTEX OpenGate;
static UNICODE_STRING Link=RTL_CONSTANT_STRING(L"\\DosDevices\\PCGuardControl");
static const GUID DeviceClass={0xc5af65eb,0x49d7,0x46cf,{0x95,0x0f,0xef,0x53,0xac,0x0e,0x87,0xa3}};
static NTSTATUS Complete(PIRP irp,NTSTATUS status,ULONG_PTR bytes){irp->IoStatus.Status=status;irp->IoStatus.Information=bytes;IoCompleteRequest(irp,IO_NO_INCREMENT);return status;}
static NTSTATUS OpenClose(PDEVICE_OBJECT device,PIRP irp){PIO_STACK_LOCATION stack=IoGetCurrentIrpStackLocation(irp);NTSTATUS status=STATUS_SUCCESS;UNREFERENCED_PARAMETER(device);
    if(stack->MajorFunction==IRP_MJ_CREATE){ExAcquireFastMutex(&OpenGate);if(Stopping)status=STATUS_DELETE_PENDING;else{++OpenHandles;stack->FileObject->FsContext=(PVOID)1;}ExReleaseFastMutex(&OpenGate);}
    if(stack->MajorFunction==IRP_MJ_CLEANUP){PcgAbortOwner(stack->FileObject);ExAcquireFastMutex(&OpenGate);if(stack->FileObject->FsContext){--OpenHandles;stack->FileObject->FsContext=NULL;}ExReleaseFastMutex(&OpenGate);}return Complete(irp,status,0);}
static NTSTATUS Unsupported(PDEVICE_OBJECT device,PIRP irp){UNREFERENCED_PARAMETER(device);return Complete(irp,STATUS_INVALID_DEVICE_REQUEST,0);}
static NTSTATUS Unload(FLT_FILTER_UNLOAD_FLAGS flags){UNREFERENCED_PARAMETER(flags);ExAcquireFastMutex(&OpenGate);if(OpenHandles){ExReleaseFastMutex(&OpenGate);return STATUS_FLT_DO_NOT_DETACH;}Stopping=TRUE;ExReleaseFastMutex(&OpenGate);PcgFreeObjects();if(CallbackRegistered){PsSetCreateProcessNotifyRoutineEx(PcgProcess,TRUE);CallbackRegistered=FALSE;}if(PcgFilter){FltUnregisterFilter(PcgFilter);PcgFilter=NULL;}IoDeleteSymbolicLink(&Link);if(Device){IoDeleteDevice(Device);Device=NULL;}PcgFreeProcesses();PcgFreeControl();if(PcgPolicy){ExFreePoolWithTag(PcgPolicy,PCG_TAG);PcgPolicy=NULL;}return STATUS_SUCCESS;}
static NTSTATUS Instance(PCFLT_RELATED_OBJECTS objects,FLT_INSTANCE_SETUP_FLAGS flags,DEVICE_TYPE type,FLT_FILESYSTEM_TYPE fs){UNREFERENCED_PARAMETER(objects);UNREFERENCED_PARAMETER(flags);UNREFERENCED_PARAMETER(type);return fs==FLT_FSTYPE_NTFS||fs==FLT_FSTYPE_REFS?STATUS_SUCCESS:STATUS_FLT_DO_NOT_ATTACH;}
static const FLT_OPERATION_REGISTRATION Operations[]={
    {IRP_MJ_CREATE,0,PcgPreOperation,PcgPostCreate,NULL},{IRP_MJ_WRITE,0,PcgPreOperation,NULL,NULL},{IRP_MJ_SET_INFORMATION,0,PcgPreOperation,NULL,NULL},{IRP_MJ_FILE_SYSTEM_CONTROL,0,PcgPreOperation,NULL,NULL},{IRP_MJ_ACQUIRE_FOR_SECTION_SYNCHRONIZATION,0,PcgPreSection,NULL,NULL},{IRP_MJ_OPERATION_END,0,NULL,NULL,NULL}
};
static const FLT_CONTEXT_REGISTRATION Contexts[]={{FLT_STREAM_CONTEXT,0,NULL,sizeof(PCG_STREAM),PCG_TAG,NULL,NULL,NULL},{FLT_CONTEXT_END,0,NULL,0,0,NULL,NULL,NULL}};
static const FLT_REGISTRATION Registration={sizeof(FLT_REGISTRATION),FLT_REGISTRATION_VERSION,0,Contexts,Operations,Unload,Instance,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL};
NTSTATUS DriverEntry(PDRIVER_OBJECT driver,PUNICODE_STRING registry){
    NTSTATUS status;ULONG i;UNICODE_STRING name=RTL_CONSTANT_STRING(L"\\Device\\PCGuardControl");UNICODE_STRING sddl=RTL_CONSTANT_STRING(L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");UNREFERENCED_PARAMETER(registry);
    ExInitializeFastMutex(&OpenGate);PcgInitializeControl();
    for(i=0;i<=IRP_MJ_MAXIMUM_FUNCTION;++i)driver->MajorFunction[i]=Unsupported;
    driver->MajorFunction[IRP_MJ_CREATE]=OpenClose;driver->MajorFunction[IRP_MJ_CLOSE]=OpenClose;driver->MajorFunction[IRP_MJ_CLEANUP]=OpenClose;driver->MajorFunction[IRP_MJ_DEVICE_CONTROL]=PcgControl;
    status=IoCreateDeviceSecure(driver,0,&name,FILE_DEVICE_UNKNOWN,FILE_DEVICE_SECURE_OPEN,FALSE,&sddl,&DeviceClass,&Device);if(!NT_SUCCESS(status))return status;
    Device->Flags|=DO_BUFFERED_IO;Device->Flags&=~DO_DEVICE_INITIALIZING;
    status=IoCreateSymbolicLink(&Link,&name);if(!NT_SUCCESS(status))goto fail;
    status=FltRegisterFilter(driver,&Registration,&PcgFilter);if(!NT_SUCCESS(status))goto fail;
    status=FltStartFiltering(PcgFilter);if(!NT_SUCCESS(status))goto fail;
    status=PcgInitializeObjects();if(!NT_SUCCESS(status))goto fail;
    status=PsSetCreateProcessNotifyRoutineEx(PcgProcess,FALSE);if(!NT_SUCCESS(status))goto fail;CallbackRegistered=TRUE;
    return STATUS_SUCCESS;
fail:Unload(0);return status;
}
