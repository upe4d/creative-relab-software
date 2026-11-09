from pathlib import Path
import argparse,hashlib,json,os,zipfile
# Пакет формируется из перечня программных файлов, личные каталоги не обходятся.
parser=argparse.ArgumentParser()
parser.add_argument('--payload',required=True,type=Path)
parser.add_argument('--output',type=Path)
parser.add_argument('--source',action='store_true')
parser.add_argument('--third-party-source',type=Path)
args=parser.parse_args()
repo=Path(__file__).resolve().parents[1]
out=args.output or repo.parent/'releases'
for p in [args.payload,out]:
 if os.name=='nt' and p.resolve().drive.upper()!='D:':raise RuntimeError('Все артефакты должны находиться на D')
out.mkdir(parents=True,exist_ok=True)
version='1.0.0';slug='pc-record'
files=[]
for name in ['PC Record.exe','app.ico','app.manifest','Creative Relab Updates.exe','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','ScreenRecorderLib.dll','WebView2Loader.dll','README.md','CHANGES.md','LICENSE.txt']:
 files.append((args.payload/name,name))
for name in ['public','tools','licenses','system']:
 for p in (args.payload/name).rglob('*'):
  if p.is_file():files.append((p,p.relative_to(args.payload).as_posix()))
for p,n in files:
 if not p.is_file() or p.is_symlink():raise RuntimeError('Недопустимый файл: '+str(p))
 if any(part.casefold() in {'data','recordings','private','webview2','helper-build'} for part in Path(n).parts):raise RuntimeError('Приватный каталог в пакете')
def archive(path,entries):
 if path.exists():
  # Разрешаем только чтение уже созданного побитово совпадающего пакета.
  expected={name:source for source,name in entries}
  with zipfile.ZipFile(path) as z:
   if len(z.namelist())!=len(expected) or set(z.namelist())!=set(expected):raise RuntimeError('Существующий выпуск имеет другое содержимое')
   if z.testzip():raise RuntimeError('Повреждён существующий архив')
   for name,source in expected.items():
    if hashlib.sha256(z.read(name)).digest()!=hashlib.sha256(source.read_bytes()).digest():raise RuntimeError('Существующий выпуск не перезаписывается: '+name)
  return {'name':path.name,'bytes':path.stat().st_size,'entries':len(expected),'sha256':hashlib.sha256(path.read_bytes()).hexdigest().upper()}
 names=set()
 with zipfile.ZipFile(path,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
  for p,n in sorted(entries,key=lambda pair:pair[1]):
   if n.casefold() in names or p.is_symlink() or '..' in Path(n).parts:raise RuntimeError('Недопустимый путь или повтор '+n)
   names.add(n.casefold());z.write(p,n)
 with zipfile.ZipFile(path) as z:
  bad=z.testzip()
  if bad:raise RuntimeError('CRC не совпала: '+bad)
 return {'name':path.name,'bytes':path.stat().st_size,'entries':len(names),'sha256':hashlib.sha256(path.read_bytes()).hexdigest().upper()}
portable=archive(out/f'{slug}-{version}-portable.zip',files)
manifest=[{'path':n,'bytes':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest().upper()} for p,n in sorted(files,key=lambda pair:pair[1])]
(out/f'{slug}-{version}-payload.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
spec={'Name':'PC Record','Slug':slug,'Version':version,'MainExe':'PC Record.exe','SHA256':portable['sha256'],'DefaultFolder':r'D:\PC Record'}
(out/'pc-record-product.json').write_text(json.dumps(spec,ensure_ascii=False,indent=2),encoding='utf-8')
result={'portable':portable}
if args.source:
 if not args.third_party_source:raise RuntimeError('Нужны соответствующие исходники FFmpeg')
 sources=[]
 for directory in ['apps/pc-record','updates','branding','portable']:
  for p in (repo/directory).rglob('*'):
   if p.is_file() and not any(part in {'.git','dependencies','build','bin','obj','Data','Recordings','__pycache__'} for part in p.relative_to(repo/directory).parts):
    if p.suffix.lower() in {'.exe','.dll','.pfx','.key','.pdb','.user','.suo'}:raise RuntimeError('Бинарный или приватный файл в исходниках: '+str(p))
    sources.append((p,p.relative_to(repo).as_posix()))
 for name in ['ffmpeg-8.1.2.tar.xz','ffmpeg-lgpl-builds-v8.1.2-27.zip','nv-codec-headers-n11.1.5.3.zip']:
  sources.append((args.third_party_source/name,'third-party-source/'+name))
 sources.append((Path(__file__),'tools/Build-PCRecordRelease.py'))
 for p in (repo/'verification').glob('PCRecord*.cs'):sources.append((p,p.relative_to(repo).as_posix()))
 sources.append((repo/'verification/PCRecordLibraryChecks.js','verification/PCRecordLibraryChecks.js'))
 sources.append((repo/'verification/PCRecordPrepareLibrary.py','verification/PCRecordPrepareLibrary.py'))
 sources.append((repo/'verification/PC-RECORD-RESULTS-RU.md','verification/PC-RECORD-RESULTS-RU.md'))
 for p in (repo/'verification/pc-record-1.0.0').iterdir():
  if p.is_file() and p.suffix in {'.json','.png'}:sources.append((p,p.relative_to(repo).as_posix()))
 result['source']=archive(out/f'{slug}-{version}-source.zip',sources)
print(json.dumps(result,ensure_ascii=False))
