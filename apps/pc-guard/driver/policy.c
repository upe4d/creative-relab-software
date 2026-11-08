#ifdef PCG_KERNEL
#include <ntddk.h>
#endif
#include "protocol.h"
static PCG_U16 Fold(PCG_U16 c) {
#ifdef PCG_KERNEL
    return RtlUpcaseUnicodeChar(c);
#else
    if(c>='a'&&c<='z')return (PCG_U16)(c-32);
    if(c>=0x430&&c<=0x44f)return (PCG_U16)(c-32);
    if(c==0x451)return 0x401;
    return c;
#endif
}
static PCG_U32 Length(const PCG_U16* s) { PCG_U32 n=0;while(n<PCG_PATH_CHARS&&s[n])++n;return n; }
static int Compare(const PCG_U8* a,const PCG_U8* b) { PCG_U32 i;for(i=0;i<32;++i){if(a[i]!=b[i])return a[i]<b[i]?-1:1;}return 0; }
const PCG_PATH* PcgExec(const PCG_HEADER* p) {return (const PCG_PATH*)(p+1);}
const PCG_PATH* PcgProtected(const PCG_HEADER* p) {return PcgExec(p)+p->ExecCount;}
const PCG_PERMIT* PcgPermits(const PCG_HEADER* p) {return (const PCG_PERMIT*)(PcgProtected(p)+p->ProtectCount);}
const PCG_U8* PcgHashes(const PCG_HEADER* p) {return (const PCG_U8*)(PcgPermits(p)+p->PermitCount);}
const PCG_PROBE* PcgProbes(const PCG_HEADER* p){return (const PCG_PROBE*)(PcgHashes(p)+p->HashCount*32u);}
int PcgSamePath(const PCG_U16* s,const PCG_PATH* p) {PCG_U32 i,n=Length(p->Text);if(n==PCG_PATH_CHARS||Length(s)!=n)return 0;for(i=0;i<n;++i)if(Fold(s[i])!=Fold(p->Text[i]))return 0;return 1;}
int PcgWithin(const PCG_U16* s,const PCG_PATH* p) {PCG_U32 i,n=Length(p->Text),m=Length(s);if(n==PCG_PATH_CHARS||m==PCG_PATH_CHARS||m<n)return 0;for(i=0;i<n;++i)if(Fold(s[i])!=Fold(p->Text[i]))return 0;return m==n||s[n]=='\\';}
static int ValidPath(const PCG_PATH* p) {
    PCG_U32 i,n=Length(p->Text),slashes=0;
    const PCG_U16 prefix[]={'\\','D','e','v','i','c','e','\\',0};
    if(n<20||n>=PCG_PATH_CHARS||p->Text[n-1]=='\\')return 0;
    for(i=0;i<8;++i)if(Fold(p->Text[i])!=Fold(prefix[i]))return 0;
    for(i=0;i<n;++i){PCG_U16 c=p->Text[i];if(c<32||c==':'||c=='/'||c=='*'||c=='?')return 0;if(c=='\\'){++slashes;if(i&&p->Text[i-1]=='\\')return 0;if(i+1<n&&p->Text[i+1]=='.'&&(i+2==n||p->Text[i+2]=='\\'||p->Text[i+2]=='.'))return 0;}}
    return slashes>=3;
}
int PcgValidate(const void* bytes,PCG_U32 length) {
    const PCG_HEADER* p=(const PCG_HEADER*)bytes;PCG_U32 expected,i;const PCG_U8* h;
    if(!p||length<sizeof(*p)||p->Version!=PCG_VERSION||(p->Flags&~7u)||p->ExecCount>PCG_MAX_ROOTS||p->ProtectCount>PCG_MAX_ROOTS||p->PermitCount>PCG_MAX_PERMITS||p->HashCount>PCG_MAX_HASHES||p->ProbeCount>PCG_MAX_PROBES)return 0;
    expected=(PCG_U32)sizeof(*p)+(p->ExecCount+p->ProtectCount)*(PCG_U32)sizeof(PCG_PATH)+p->PermitCount*(PCG_U32)sizeof(PCG_PERMIT)+p->HashCount*32u+p->ProbeCount*(PCG_U32)sizeof(PCG_PROBE);
    if(length!=expected||((p->Flags&PCG_FLAG_EXEC)&&(!p->ExecCount||!p->HashCount))||((p->Flags&PCG_FLAG_WRITE)&&!p->ProtectCount))return 0;
    for(i=0;i<p->ExecCount+p->ProtectCount;++i)if(!ValidPath(PcgExec(p)+i))return 0;
    for(i=0;i<p->PermitCount;++i)if(!ValidPath(&PcgPermits(p)[i].Path))return 0;
    h=PcgHashes(p);for(i=1;i<p->HashCount;++i)if(Compare(h+(i-1)*32,h+i*32)>=0)return 0;
    for(i=0;i<p->ProbeCount;++i)if(!PcgProbes(p)[i].Handle||(PcgProbes(p)[i].Handle>>32))return 0;
    return 1;
}
int PcgIdCompare(const PCG_ID* a,const PCG_ID* b){PCG_U32 i;if(a->Volume!=b->Volume)return a->Volume<b->Volume?-1:1;for(i=0;i<16;++i)if(a->Id[i]!=b->Id[i])return a->Id[i]<b->Id[i]?-1:1;return 0;}
int PcgIdContains(const PCG_ID* ids,PCG_U32 count,const PCG_ID* id){PCG_U32 lo=0,hi=count;while(lo<hi){PCG_U32 mid=lo+(hi-lo)/2;int result=PcgIdCompare(ids+mid,id);if(!result)return 1;if(result<0)lo=mid+1;else hi=mid;}return 0;}
int PcgBlocked(const PCG_HEADER* p,const PCG_U8 hash[32]) {
    PCG_U32 lo=0,hi=p->HashCount;const PCG_U8* h=PcgHashes(p);
    while(lo<hi){PCG_U32 mid=lo+(hi-lo)/2;int cmp=Compare(h+mid*32,hash);if(!cmp)return 1;if(cmp<0)lo=mid+1;else hi=mid;}
    return 0;
}
