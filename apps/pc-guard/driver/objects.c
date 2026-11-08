#include "guard.h"
/* Публичные access masks из SDK winnt.h; WDK не объявляет эти имена для kernel C. */
#define PCG_PROCESS_CREATE_THREAD 0x0002u
#define PCG_PROCESS_VM_OPERATION 0x0008u
#define PCG_PROCESS_VM_WRITE 0x0020u
#define PCG_PROCESS_CREATE_PROCESS 0x0080u
#define PCG_THREAD_SET_THREAD_TOKEN 0x0080u
#define PCG_THREAD_IMPERSONATE 0x0100u
#define PCG_THREAD_DIRECT_IMPERSONATION 0x0200u

/* Право редактора нельзя переносить в чужой процесс через кражу handle или внедрение кода. */
static PVOID ObjectRegistration;
/* Экспорт проверяется при загрузке; фиксированные смещения EPROCESS не используются.
   Этот дополнительный NT API требует отдельной проверки совместимости перед выпуском. */
typedef HANDLE (NTAPI *PCG_PARENT_ID)(PEPROCESS process);
static PCG_PARENT_ID ParentId;
static BOOLEAN Enabled(VOID){BOOLEAN enabled;PcgReadLock();enabled=PcgPolicy&&(PcgPolicy->Flags&PCG_FLAG_WRITE)&&PcgPolicy->ProtectCount&&PcgPolicy->PermitCount;PcgReadUnlock();return enabled;}
static BOOLEAN Editor(PEPROCESS process){
    PUNICODE_STRING name=NULL;BOOLEAN editor=FALSE;NTSTATUS status;ULONG i;
    if(PcgPermittedProcess(process)||PcgControllerProcess(process))return TRUE;
    status=SeLocateProcessImageName(process,&name);
    /* Пока образ нового процесса ещё неизвестен, опасные права не выдаются заранее. */
    if(!NT_SUCCESS(status)){InterlockedIncrement(&PcgErrors);return TRUE;}
    PcgReadLock();if(PcgPolicy)for(i=0;i<PcgPolicy->PermitCount;++i){const PCG_PATH* path=&PcgPermits(PcgPolicy)[i].Path;UNICODE_STRING other;SIZE_T n=0;while(n<PCG_PATH_CHARS&&path->Text[n])++n;other.Buffer=(PWCH)path->Text;other.Length=(USHORT)(n*2);other.MaximumLength=other.Length;if(RtlEqualUnicodeString(name,&other,TRUE)){editor=TRUE;break;}}PcgReadUnlock();ExFreePool(name);return editor;
}
static OB_PREOP_CALLBACK_STATUS ObjectPre(PVOID context,POB_PRE_OPERATION_INFORMATION info){
    PEPROCESS target;ACCESS_MASK remove,*access;BOOLEAN isProcess;UNREFERENCED_PARAMETER(context);
    if(info->KernelHandle||ExGetPreviousMode()==KernelMode||!Enabled())return OB_PREOP_SUCCESS;
    isProcess=info->ObjectType==*PsProcessType;target=isProcess?(PEPROCESS)info->Object:PsGetThreadProcess((PETHREAD)info->Object);
    if(target==PsGetCurrentProcess()||PcgTrustedCreator(PsGetCurrentProcess())||!Editor(target))return OB_PREOP_SUCCESS;
    /* Родителю нужны начальные handles обычного ребёнка. Такой ребёнок не получает
       право редактора: оно выдаётся только после SHA и доверенного фактического создателя. */
    if(info->Operation==OB_OPERATION_HANDLE_CREATE&&!PcgPermittedProcess(target)&&!PcgControllerProcess(target)&&ParentId(target)==PsGetProcessId(PsGetCurrentProcess()))return OB_PREOP_SUCCESS;
    remove=isProcess?(PROCESS_DUP_HANDLE|PCG_PROCESS_VM_OPERATION|PCG_PROCESS_VM_WRITE|PCG_PROCESS_CREATE_THREAD|PCG_PROCESS_CREATE_PROCESS):(THREAD_SET_CONTEXT|PCG_THREAD_SET_THREAD_TOKEN|PCG_THREAD_IMPERSONATE|PCG_THREAD_DIRECT_IMPERSONATION);
    access=info->Operation==OB_OPERATION_HANDLE_CREATE?&info->Parameters->CreateHandleInformation.DesiredAccess:&info->Parameters->DuplicateHandleInformation.DesiredAccess;
    if(*access&remove){*access&=~remove;InterlockedIncrement(&PcgWrites);}return OB_PREOP_SUCCESS;
}
NTSTATUS PcgInitializeObjects(VOID){
    OB_CALLBACK_REGISTRATION registration;OB_OPERATION_REGISTRATION operations[2];UNICODE_STRING altitude=RTL_CONSTANT_STRING(L"329900.712346");
    {UNICODE_STRING routine=RTL_CONSTANT_STRING(L"PsGetProcessInheritedFromUniqueProcessId");ParentId=(PCG_PARENT_ID)MmGetSystemRoutineAddress(&routine);if(!ParentId)return STATUS_PROCEDURE_NOT_FOUND;}
    RtlZeroMemory(&registration,sizeof(registration));RtlZeroMemory(operations,sizeof(operations));
    operations[0].ObjectType=PsProcessType;operations[0].Operations=OB_OPERATION_HANDLE_CREATE|OB_OPERATION_HANDLE_DUPLICATE;operations[0].PreOperation=ObjectPre;
    operations[1].ObjectType=PsThreadType;operations[1].Operations=OB_OPERATION_HANDLE_CREATE|OB_OPERATION_HANDLE_DUPLICATE;operations[1].PreOperation=ObjectPre;
    registration.Version=OB_FLT_REGISTRATION_VERSION;registration.OperationRegistrationCount=2;registration.Altitude=altitude;registration.OperationRegistration=operations;
    return ObRegisterCallbacks(&registration,&ObjectRegistration);
}
VOID PcgFreeObjects(VOID){if(ObjectRegistration){ObUnRegisterCallbacks(ObjectRegistration);ObjectRegistration=NULL;}}
