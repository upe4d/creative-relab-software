from pathlib import Path
import argparse,hashlib,json,zipfile,os
p=argparse.ArgumentParser();p.add_argument('--payload',type=Path,required=True);p.add_argument('--source',action='store_true');a=p.parse_args();repo=Path(__file__).resolve().parents[1];suite=repo.parent;src=repo/'apps/pc-guard';out=suite/'releases';version='3.5.1'
if a.payload.resolve().drive.upper()!='D:':raise RuntimeError('Только диск D')
def digest(p):return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def archive(path,items):
 names=[n for _,n in items]
 if len(set(n.lower() for n in names))!=len(names):raise RuntimeError('Duplicate path')
 if path.exists():
  with zipfile.ZipFile(path) as z:
   if set(z.namelist())!=set(names) or z.testzip():raise RuntimeError('Existing archive differs')
   for f,n in items:
    if hashlib.sha256(z.read(n)).hexdigest().upper()!=digest(f):raise RuntimeError('Existing archive differs: '+n)
 else:
  with zipfile.ZipFile(path,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
   for f,n in sorted(items,key=lambda x:x[1]):
    if f.is_symlink() or '..' in Path(n).parts:raise RuntimeError('Unsafe path')
    z.write(f,n)
  with zipfile.ZipFile(path) as z:
   if z.testzip():raise RuntimeError('CRC failed')
 return {'file':path.name,'sha256':digest(path),'bytes':path.stat().st_size,'entries':len(items)}
files=[(a.payload/n,n) for n in ['PC Guard.exe','Creative Relab Updates.exe','app.ico','app.manifest','README.txt','THIRD-PARTY.txt','ROADMAP.md']]+[(src/'LICENSE.txt','LICENSE.txt')]
for folder in ['public','system','rules']:
 files.extend((f,folder+'/'+f.relative_to(src/folder).as_posix()) for f in (src/folder).rglob('*') if f.is_file())
for f in (src/'driver').iterdir():
 if f.is_file() and f.suffix.lower() in {'.ps1','.md','.json','.c','.h','.in'}:files.append((f,'driver/'+f.name))
sysfile=Path(r'D:\Codex\work\PCGuard-v5\driver\build\PCGuardFilter.sys')
if digest(sysfile)!='144F2450F7327975BB2D8A34D32F517776B0BC33547850FECCD0EC3E1D83B1A2':raise RuntimeError('Unexpected driver binary')
files.append((sysfile,'driver/build/PCGuardFilter.sys'))
portable=archive(out/f'pc-guard-{version}-portable.zip',files);spec={'Name':'PC Guard','Slug':'pc-guard','Version':version,'MainExe':'PC Guard.exe','SHA256':portable['sha256'],'DefaultFolder':r'D:\PC Guard'};(out/'pc-guard-product.json').write_text(json.dumps(spec,ensure_ascii=False,indent=2),encoding='utf-8');result={'portable':portable}
if a.source:
 items=[];skip={'Data','dependencies','bin','obj','build','node_modules','.git','__pycache__','private'}
 for folder in ['apps/pc-guard','updates','branding','portable']:
  for f in (repo/folder).rglob('*'):
   if not f.is_file() or any(x in skip for x in f.relative_to(repo/folder).parts):continue
   if f.suffix.lower() in {'.exe','.dll','.pfx','.key','.pdb','.obj','.user','.suo','.sys'} or '.before-' in f.name:continue
   items.append((f,f.relative_to(repo).as_posix()))
 items.append((Path(__file__),'tools/Build-PCGuardRelease.py'))
 items.append((repo/'verification/PC-GUARD-3.5.1-RESULTS-RU.md','verification/PC-GUARD-3.5.1-RESULTS-RU.md'))
 for f in (repo/'verification/pc-guard-3.5.1').iterdir():
  if f.is_file() and f.suffix in {'.json','.png'}:items.append((f,f.relative_to(repo).as_posix()))
 result['source']=archive(out/f'pc-guard-{version}-source.zip',items)
print(json.dumps(result,ensure_ascii=False))