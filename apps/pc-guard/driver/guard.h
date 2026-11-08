#pragma once
#include <fltKernel.h>
#include <bcrypt.h>
#include <wdmsec.h>
#include "protocol.h"
#define PCG_TAG '3gcP'
#define PCG_IOCTL_STATUS CTL_CODE(FILE_DEVICE_UNKNOWN,0x900,METHOD_BUFFERED,FILE_READ_DATA)
#define PCG_IOCTL_CONFIG CTL_CODE(FILE_DEVICE_UNKNOWN,0x901,METHOD_BUFFERED,FILE_WRITE_DATA)
#define PCG_IOCTL_COMMIT CTL_CODE(FILE_DEVICE_UNKNOWN,0x902,METHOD_BUFFERED,FILE_WRITE_DATA)
#define PCG_IOCTL_ABORT CTL_CODE(FILE_DEVICE_UNKNOWN,0x903,METHOD_BUFFERED,FILE_WRITE_DATA)
#define PCG_IOCTL_BROKER CTL_CODE(FILE_DEVICE_UNKNOWN,0x904,METHOD_BUFFERED,FILE_WRITE_DATA)
extern PFLT_FILTER PcgFilter;
extern EX_PUSH_LOCK PcgLock;
extern PCG_HEADER* PcgPolicy;
extern PCG_HEADER* PcgPending;
extern PCG_ID* PcgIds;
extern ULONG PcgIdCount;
extern volatile LONG PcgGeneration,PcgStarts,PcgWrites,PcgErrors;
BOOLEAN PcgProtectedVolume(PCUNICODE_STRING name);
BOOLEAN PcgProtectedName(PCUNICODE_STRING name);
BOOLEAN PcgProtectedAncestor(PCUNICODE_STRING name);
BOOLEAN PcgPendingName(PCUNICODE_STRING name,BOOLEAN ancestors);
BOOLEAN PcgProtectedId(const PCG_ID* id);
BOOLEAN PcgExecutionName(PCUNICODE_STRING name);
BOOLEAN PcgNameWithin(PCUNICODE_STRING name,const PCG_PATH* root);
NTSTATUS PcgQueryId(PFLT_INSTANCE instance,PFILE_OBJECT file,PCG_ID* id);
VOID PcgInitializeControl(VOID);
VOID PcgAbortOwner(PFILE_OBJECT owner);
VOID PcgFreeControl(VOID);
FLT_POSTOP_CALLBACK_STATUS PcgPostCreate(PFLT_CALLBACK_DATA data,PCFLT_RELATED_OBJECTS objects,PVOID context,FLT_POST_OPERATION_FLAGS flags);
typedef struct {PCG_ID Id;BOOLEAN HasId;PCG_PATH Name;KSPIN_LOCK Lock;BOOLEAN HashValid;PCG_U8 Hash[32];LONG Epoch;} PCG_STREAM;
NTSTATUS PcgGetStream(PCFLT_RELATED_OBJECTS objects,PCG_STREAM** result);
VOID PcgInvalidateStream(PCG_STREAM* stream);
BOOLEAN PcgStreamProtected(PCG_STREAM* stream);
FLT_PREOP_CALLBACK_STATUS PcgPreSection(PFLT_CALLBACK_DATA data,PCFLT_RELATED_OBJECTS objects,PVOID* context);
NTSTATUS PcgInitializeObjects(VOID);
VOID PcgFreeObjects(VOID);
BOOLEAN PcgPermittedProcess(PEPROCESS process);
BOOLEAN PcgControllerProcess(PEPROCESS process);
BOOLEAN PcgTrustedCreator(PEPROCESS process);
VOID PcgSetController(PEPROCESS process);
NTSTATUS PcgHashFile(PFILE_OBJECT file,PCG_U8 output[32]);
VOID PcgProcess(PEPROCESS process,HANDLE id,PPS_CREATE_NOTIFY_INFO info);
VOID PcgFreeProcesses(VOID);
FLT_PREOP_CALLBACK_STATUS PcgPreOperation(PFLT_CALLBACK_DATA data,PCFLT_RELATED_OBJECTS objects,PVOID* context);
NTSTATUS PcgControl(PDEVICE_OBJECT device,PIRP irp);
static __forceinline VOID PcgReadLock(VOID){KeEnterCriticalRegion();ExAcquirePushLockShared(&PcgLock);}
static __forceinline VOID PcgReadUnlock(VOID){ExReleasePushLockShared(&PcgLock);KeLeaveCriticalRegion();}
