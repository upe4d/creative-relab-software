"""Упаковка PC Care по явному списку, без пользовательских Data и старых релизов."""
from pathlib import Path
import hashlib,json,re,zipfile
repo=Path(__file__).resolve().parents[1];root=repo.parent
if root.drive.upper()!='D:':raise RuntimeError('Use D only')
app=repo/'apps/pc-care';v=re.search(r'AssemblyFileVersion\("(\d+\.\d+\.\d+)\.0"\)',(app/'Source/AssemblyInfo.cs').read_text(encoding='utf-8-sig')).group(1)
out=root/'releases';out.mkdir(exist_ok=True)
def pack(name,items):
 target=out/name
 with zipfile.ZipFile(target,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
  for src,dst in sorted(items,key=lambda x:x[1]):
   if any(s.lower() in {'data','private','recordings'} for s in Path(dst).parts):raise RuntimeError('Private path')
   z.write(src,dst)
 return hashlib.sha256(target.read_bytes()).hexdigest().upper()
files=[(root/f'builds/pc-care-{v}/PC Care.exe','PC Care.exe'),(root/'builds/updates/Creative Relab Updates.exe','Creative Relab Updates.exe')]+[(app/n,n) for n in ['README.txt','LICENSE.txt','CHANGES.txt']]
sha=pack(f'pc-care-{v}-portable.zip',files)
source=[(app/n,n) for n in ['README.txt','LICENSE.txt','CHANGES.txt']]
for folder,prefix in [(app/'Source','Source/'),(repo/'updates','updates/'),(repo/'branding','branding/')]:
 for f in folder.rglob('*'):
  if f.is_file() and not any(x.lower() in {'data','private','bin','obj'} for x in f.relative_to(folder).parts) and f.suffix.lower() in {'.cs','.ps1','.xaml','.manifest','.ico','.png','.md','.json','.txt'}:source.append((f,prefix+f.relative_to(folder).as_posix()))
pack(f'pc-care-{v}-source.zip',source)
(out/f'pc-care-{v}-product.json').write_text(json.dumps({'Name':'PC Care','Slug':'pc-care','Version':v,'MainExe':'PC Care.exe','SHA256':sha,'DefaultFolder':r'D:\PC Care'},ensure_ascii=False),encoding='utf-8')
print(json.dumps({'version':v,'portableSHA256':sha,'sourceFiles':len(source)}))
