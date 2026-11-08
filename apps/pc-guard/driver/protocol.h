#pragma once
/* Формат ограничен по размеру; один и тот же разбор используется драйвером и стендом. */
typedef unsigned char PCG_U8;
typedef unsigned short PCG_U16;
typedef unsigned int PCG_U32;
typedef unsigned long long PCG_U64;
#define PCG_VERSION 4u
#define PCG_MAX_PROBES 2000u
#define PCG_MAX_ROOTS 16u
#define PCG_MAX_PERMITS 64u
#define PCG_MAX_HASHES 32768u
#define PCG_PATH_CHARS 512u
#define PCG_FLAG_EXEC 1u
#define PCG_FLAG_WRITE 2u
#define PCG_FLAG_FAIL_CLOSED 4u
#pragma pack(push, 1)
typedef struct { PCG_U32 Version, Flags, HashCount, ExecCount, ProtectCount, PermitCount, ProbeCount; } PCG_HEADER;
typedef struct { PCG_U16 Text[PCG_PATH_CHARS]; } PCG_PATH;
typedef struct { PCG_PATH Path; PCG_U8 Hash[32]; } PCG_PERMIT;
typedef struct { PCG_U64 Handle; } PCG_PROBE;
typedef struct { PCG_U64 Volume;PCG_U8 Id[16]; } PCG_ID;
typedef struct { PCG_U32 Version, Flags, HashCount, ExecCount, ProtectCount, PermitCount, Generation, BlockedStarts, BlockedWrites, ScanErrors, VerifiedProcesses, Preparing, ProtectedIds; } PCG_STATUS;
#pragma pack(pop)
int PcgValidate(const void* bytes, PCG_U32 length);
int PcgWithin(const PCG_U16* path, const PCG_PATH* root);
int PcgSamePath(const PCG_U16* path, const PCG_PATH* other);
const PCG_PATH* PcgExec(const PCG_HEADER* p);
const PCG_PATH* PcgProtected(const PCG_HEADER* p);
const PCG_PERMIT* PcgPermits(const PCG_HEADER* p);
const PCG_U8* PcgHashes(const PCG_HEADER* p);
int PcgBlocked(const PCG_HEADER* p, const PCG_U8 hash[32]);
const PCG_PROBE* PcgProbes(const PCG_HEADER* p);
int PcgIdCompare(const PCG_ID* a,const PCG_ID* b);
int PcgIdContains(const PCG_ID* ids,PCG_U32 count,const PCG_ID* id);
