#include "protocol.h"
__declspec(dllimport) void __stdcall ExitProcess(unsigned int code);
__declspec(dllimport) void* __stdcall GetStdHandle(unsigned int value);
__declspec(dllimport) int __stdcall WriteFile(void* handle,const void* bytes,unsigned int size,unsigned int* wrote,void* overlapped);
__declspec(dllimport) void* __stdcall VirtualAlloc(void* address,unsigned __int64 size,unsigned int allocation,unsigned int protect);
__declspec(dllimport) int __stdcall VirtualProtect(void* address,unsigned __int64 size,unsigned int protect,unsigned int* old);
__declspec(dllimport) int __stdcall VirtualFree(void* address,unsigned __int64 size,unsigned int operation);
static PCG_U8 Buffer[sizeof(PCG_HEADER)+2*sizeof(PCG_PATH)+2*sizeof(PCG_PERMIT)+128];
static unsigned int Checks,Failures;
static void Zero(void* data,unsigned int size){volatile PCG_U8* p=(volatile PCG_U8*)data;while(size--)*p++=0;}
static void Path(PCG_PATH* p,const PCG_U16* text){unsigned int i;Zero(p,sizeof(*p));for(i=0;i<PCG_PATH_CHARS-1&&text[i];++i)p->Text[i]=text[i];}
static void Check(int ok){++Checks;if(!ok)++Failures;}
static unsigned int Seed=1234567;
static unsigned int Random(void){Seed=Seed*1664525u+1013904223u;return Seed;}
static char Output[256];static unsigned int OutputLength;
static void Text(const char* text){while(*text)Output[OutputLength++]=*text++;}
static void Number(unsigned int n){char digits[16];unsigned int i=0;do{digits[i++]=(char)('0'+n%10);n/=10;}while(n);while(i)Output[OutputLength++]=digits[--i];}
void mainCRTStartup(void){
    PCG_HEADER* p=(PCG_HEADER*)Buffer;PCG_PATH* root;PCG_U8* hashes;unsigned int length,i,j,wrote=0,oldProtection;PCG_U8 key[32];PCG_U8* pages;
    Zero(Buffer,sizeof(Buffer));p->Version=PCG_VERSION;Check(PcgValidate(p,sizeof(*p)));Check(!PcgValidate(p,sizeof(*p)-1));
    p->Flags=0x8000;Check(!PcgValidate(p,sizeof(*p)));p->Flags=PCG_FLAG_EXEC;Check(!PcgValidate(p,sizeof(*p)));
    p->ExecCount=1;p->HashCount=2;p->Flags|=PCG_FLAG_FAIL_CLOSED;root=(PCG_PATH*)(p+1);Path(root,(const PCG_U16*)L"\\Device\\HarddiskVolume4\\Downloads");hashes=(PCG_U8*)(root+1);hashes[31]=1;hashes[63]=2;length=sizeof(*p)+sizeof(PCG_PATH)+64;
    Check(PcgValidate(p,length));Check(PcgWithin((const PCG_U16*)L"\\device\\HARDDISKVOLUME4\\downloads\\a.exe",root));Check(!PcgWithin((const PCG_U16*)L"\\Device\\HarddiskVolume4\\Downloads-other\\a.exe",root));
    Check(PcgWithin(root->Text,root));Check(!PcgSamePath((const PCG_U16*)L"\\Device\\HarddiskVolume4\\Downloads\\a.exe",root));
    Zero(key,sizeof(key));key[31]=1;Check(PcgBlocked(p,key));key[31]=2;Check(PcgBlocked(p,key));key[31]=3;Check(!PcgBlocked(p,key));
    hashes[63]=1;Check(!PcgValidate(p,length));hashes[63]=0;Check(!PcgValidate(p,length));hashes[63]=2;
    Path(root,(const PCG_U16*)L"\\Device\\HarddiskVolume4\\Downloads\\..");Check(!PcgValidate(p,length));Path(root,(const PCG_U16*)L"\\Device\\HarddiskVolume4");Check(!PcgValidate(p,length));
    Path(root,(const PCG_U16*)L"\\Device\\HarddiskVolume4\\Downloads:stream");Check(!PcgValidate(p,length));
    for(i=0;i<PCG_PATH_CHARS;++i)root->Text[i]='a';Check(!PcgValidate(p,length));
    p->HashCount=0xffffffffu;Check(!PcgValidate(p,length));p->HashCount=2;Path(root,(const PCG_U16*)L"\\Device\\HarddiskVolume4\\Downloads");
    {PCG_PROBE* probe=(PCG_PROBE*)(hashes+64);PCG_ID ids[2],needle;Zero(ids,sizeof(ids));ids[0].Volume=1;ids[0].Id[15]=7;ids[1].Volume=2;ids[1].Id[15]=7;needle=ids[1];Check(PcgIdContains(ids,2,&needle));needle.Volume=3;Check(!PcgIdContains(ids,2,&needle));needle=ids[1];needle.Id[14]=1;Check(!PcgIdContains(ids,2,&needle));Check(!PcgIdContains(ids,0,&needle));
        p->ProbeCount=1;probe->Handle=16;Check(PcgValidate(p,length+8));probe->Handle=0;Check(!PcgValidate(p,length+8));probe->Handle=0x100000010ULL;Check(!PcgValidate(p,length+8));p->ProbeCount=2001;Check(!PcgValidate(p,length+8));p->ProbeCount=0;p->Version=3;Check(!PcgValidate(p,length));p->Version=PCG_VERSION;}
    /* Запретная страница сразу после входа выявляет чтение за заявленной границей. */
    pages=(PCG_U8*)VirtualAlloc(0,16384,0x3000,4);Check(pages!=0);if(!pages)ExitProcess(2);
    Check(VirtualProtect(pages+12288,4096,1,&oldProtection)!=0);
    for(i=0;i<=length;++i){PCG_U8* tail=pages+12288-i;for(j=0;j<i;++j)tail[j]=Buffer[j];Check(PcgValidate(tail,i)==(i==length));}
    for(i=0;i<100000;++i){
        unsigned int size=Random()%(length+1),offset;PCG_U8* tail=pages+12288-size;
        for(j=0;j<size;++j)tail[j]=Buffer[j];if(size){offset=Random()%size;tail[offset]=(PCG_U8)Random();}
        (void)PcgValidate(tail,size);
    }
    Check(VirtualFree(pages,0,0x8000)!=0);Check(PcgValidate(p,length));Text("{\"checks\":");Number(Checks);Text(",\"failures\":");Number(Failures);Text(",\"fuzzCases\":100000,\"guardPage\":true,\"kernelLoaded\":false}\n");
    WriteFile(GetStdHandle((unsigned int)-11),Output,OutputLength,&wrote,0);ExitProcess(Failures?1:0);
}
