from pathlib import Path
import argparse,shutil,zlib,struct,binascii,os,json,hashlib
parser=argparse.ArgumentParser();parser.add_argument('--payload',required=True,type=Path);parser.add_argument('--target',required=True,type=Path);a=parser.parse_args();source=a.payload;target=a.target
if source.resolve().drive.upper()!='D:' or target.resolve().drive.upper()!='D:':raise RuntimeError('Используйте диск D')
if target.exists():raise RuntimeError('Нужна новая пустая папка испытаний')
target.mkdir(exist_ok=True)
for p in source.iterdir():
 if p.is_file():shutil.copy2(p,target/p.name)
for name in ['public','tools','licenses','system']:shutil.copytree(source/name,target/name,dirs_exist_ok=True)
records=target/'Recordings';records.mkdir(exist_ok=True)
def chunk(name,data):return struct.pack('!I',len(data))+name+data+struct.pack('!I',binascii.crc32(name+data)&0xffffffff)
for i in range(1005):
 image=b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('!2I5B',2,2,8,2,0,0,0))+chunk(b'IDAT',zlib.compress(b'\x00'+bytes([i%256,100,200])*2+b'\x00'+bytes([i%256,100,200])*2))+chunk(b'tEXt',b'Test\x00'+b'x'*((i*37)%2000))+chunk(b'IEND',b'')
 p=records/f'Файл {i:04d}.png';p.write_bytes(image);os.utime(p,(1770000000+i,1770000000+i))
driver=Path(__file__).with_name('PCRecordLibraryChecks.js')
shutil.copy2(driver,target/'public/assets/verify-controls.js')
proof={'fixtures':1005,'productionExeSHA256':hashlib.sha256((target/'PC Record.exe').read_bytes()).hexdigest().upper(),'productionAppJsSHA256':hashlib.sha256((target/'public/assets/native-app.js').read_bytes()).hexdigest().upper(),'driverOverride':'Only verify-controls.js in isolated test copy'}
(target/'fixture-proof.json').write_text(json.dumps(proof,indent=2),encoding='utf-8')
print(json.dumps(proof))

