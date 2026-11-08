from pathlib import Path
import zipfile,json,hashlib,os,re,xml.etree.ElementTree as ET
repo=Path(__file__).resolve().parents[1]
root=repo.parent
if os.name=='nt' and root.drive.upper()!='D:':raise RuntimeError('Release artifacts must stay on D')
out=root/'releases';out.mkdir(exist_ok=True)
excluded={'Data','bin','obj','.git','.vs','.rtk','node_modules','TestResults','__pycache__'}
def archive(path,files):
 seen=set()
 with zipfile.ZipFile(path,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
  for source,name in sorted(files,key=lambda pair:pair[1]):
   if name.casefold() in seen:raise RuntimeError('Duplicate '+name)
   seen.add(name.casefold());z.write(source,name)
 return {'bytes':path.stat().st_size,'sha256':hashlib.sha256(path.read_bytes()).hexdigest().upper(),'entries':len(seen)}
def sourcefiles(folder):
 for directory,dirs,files in os.walk(folder):
  dirs[:]=[d for d in dirs if d not in excluded]
  for name in files:
   p=Path(directory)/name
   if p.suffix.lower() in {'.pfx','.key','.pdb','.obj','.suo','.user'} or name=='selected-results.xml':continue
   yield p,p.relative_to(folder).as_posix()
def assemblyversion(file):
 text=file.read_text(encoding='utf-8-sig');version=re.search(r'AssemblyFileVersion\("([0-9.]+)"\)',text).group(1)
 return '.'.join(version.split('.')[:3])
products=[('pc-care','PC Care',assemblyversion(repo/'apps/pc-care/Source/AssemblyInfo.cs')),('pc-guard','PC Guard',assemblyversion(repo/'apps/pc-guard/native/AssemblyInfo.cs')),('pc-tune','PC Tune',ET.parse(repo/'apps/pc-tune/optimizerDuck/optimizerDuck.csproj').getroot().findtext('./PropertyGroup/Version'))]
result=[]
for slug,name,version in products:
 src=repo/'apps'/slug
 if slug=='pc-care':
  files=[(root/('builds/pc-care-'+version)/'PC Care.exe','PC Care.exe')]+[(src/n,n) for n in ['README.txt','LICENSE.txt','CHANGES.txt']]
 elif slug=='pc-guard':
  files=[(root/('builds/pc-guard-'+version)/'PC Guard.exe','PC Guard.exe')]+[(src/n,n) for n in ['README.txt','LICENSE.txt','ROADMAP.md','THIRD-PARTY.txt']]
  for directory in ['rules','system','public']:
   files.extend((p,directory+'/'+n) for p,n in sourcefiles(src/directory))
  for p in (src/'driver').iterdir():
   if p.is_file() and p.suffix.lower() in {'.ps1','.md','.json','.c','.h','.in'}:files.append((p,'driver/'+p.name))
  files.append((Path(r'D:\Codex\work\PCGuard-v5\driver\build\PCGuardFilter.sys'),'driver/build/PCGuardFilter.sys'))
 else:
  payload=root/('builds/pc-tune-'+version)
  # Включаем новую сборку оформления; результаты LTSC предыдущего бинарника хранятся отдельно.
  files=[]
  docs=['LICENSE','THIRD-PARTY-NOTICES.md','UPSTREAM.txt','README-PC-TUNE-RU.md','CHANGES-PC-TUNE-RU.md']
  for p,n in sourcefiles(payload):
   if n not in docs and not n.endswith('-source.zip') and n!='VERIFICATION.json':files.append((p,n))
  files.extend((src/n,n) for n in docs)
  if not any(n=='PC Tune.exe' for p,n in files):files.append((payload/'optimizerDuck.exe','PC Tune.exe'))
 helper=root/'builds/updates/Creative Relab Updates.exe'
 files=[(p,n) for p,n in files if n!='Creative Relab Updates.exe']
 files.append((helper,'Creative Relab Updates.exe'))
 package=out/f'{slug}-{version}-portable.zip';info=archive(package,files)
 spec={'Name':name,'Slug':slug,'Version':version,'MainExe':name+'.exe','SHA256':info['sha256'],'DefaultFolder':'D:\\'+name}
 (out/f'{slug}-product.json').write_text(json.dumps(spec,ensure_ascii=False,indent=2),encoding='utf-8')
 source=out/f'{slug}-{version}-source.zip';sourceinfo=archive(source,list(sourcefiles(src))+[(p,'updates/'+n) for p,n in sourcefiles(repo/'updates')]+[(repo/'branding'/n,'branding/'+n) for n in ['creative-relab.ico','creative-relab-avatar.png','README.md']])
 with zipfile.ZipFile(package) as z:
  if any(n.lower().startswith('data/') for n in z.namelist()):raise RuntimeError('Private Data in package')
 result.append({'product':slug,'version':version,'portable':info,'source':sourceinfo})
(root/'verification/package-builds.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result))
