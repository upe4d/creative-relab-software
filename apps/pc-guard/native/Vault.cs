using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PCGuard
{
    // Формат: заголовок + AES-256-CBC + HMAC-SHA256. MAC проверяется до выдачи открытых данных.
    internal sealed class Vault
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PCGVAUL2");
        internal const int Iterations = 600000;
        private const int HeaderLength = 8 + 4 + 32 + 16;
        private readonly Store store;
        internal Vault(Store store) { this.store=store; Directory.CreateDirectory(Path.Combine(store.Data,"Vault")); }
        internal static byte[] Random(int length) { var value=new byte[length]; using(var rng=RandomNumberGenerator.Create()) rng.GetBytes(value); return value; }
        private static byte[] Key(string password,byte[] salt,int iterations)
        {
            if (password==null||password.Length<12||password.Length>1024) throw new ArgumentException("Пароль должен содержать от 12 до 1024 символов.");
            using(var kdf=new Rfc2898DeriveBytes(password,salt,iterations,HashAlgorithmName.SHA256)) return kdf.GetBytes(64);
        }
        private static void Clear(byte[] value) { if(value!=null) System.Array.Clear(value,0,value.Length); }
        internal static bool Equal(byte[] a,byte[] b) { if(a.Length!=b.Length)return false;int value=0;for(int i=0;i<a.Length;i++)value|=a[i]^b[i];return value==0; }
        internal object List()
        {
            return Directory.GetFiles(Path.Combine(store.Data,"Vault"),"*.pcgv").Select(p=>Store.Map("id",Path.GetFileNameWithoutExtension(p),"size",new FileInfo(p).Length,"created",File.GetCreationTimeUtc(p).ToString("o"))).ToList();
        }
        internal object Encrypt(string source,string password)
        {
            source=SafeFiles.Full(source); if(SafeFiles.Within(source,store.Data))throw new IOException("Для сейфа выбери исходный файл вне данных PC Guard.");
            string id=Guid.NewGuid().ToString("N"),target=Path.Combine(store.Data,"Vault",id+".pcgv");
            byte[] salt=Random(32),iv=Random(16),key=null;
            try
            {
                key=Key(password,salt,Iterations);
                using(var input=SafeFiles.OpenStable(source,false))
                {
                    string hash=SafeFiles.Hash(input);input.Position=0;
                    var metadata=Store.Map("name",Path.GetFileName(source),"size",input.Length,"sha256",hash,"created",DateTimeOffset.Now.ToString("o"));
                    Seal(input,target,key,salt,iv,Iterations,metadata);
                    using(var sealedFile=SafeFiles.OpenStable(target,false)) Verify(sealedFile,key);
                }
                store.Journal("Сейф","Выполнено","Создан контейнер "+id+". Исходный файл сохранён.");
                return Store.Map("id",id,"path",target,"originalPreserved",true);
            }
            finally {Clear(key);Clear(salt);Clear(iv);}
        }
        internal object Decrypt(string id,string destination,string password)
        {
            if(!System.Text.RegularExpressions.Regex.IsMatch(id??"",@"\A[a-f0-9]{32}\z"))throw new ArgumentException("Неверный идентификатор сейфа.");
            string source=Path.Combine(store.Data,"Vault",id+".pcgv"); destination=SafeFiles.Full(destination);
            if(!Directory.Exists(destination))throw new IOException("Папка восстановления отсутствует.");SafeFiles.Ordinary(destination);
            byte[] key=null;
            try
            {
                using(var input=SafeFiles.OpenStable(source,false))
                {
                    var header=ReadHeader(input);key=Key(password,header.Salt,header.Iterations);Verify(input,key);
                    object result=Unseal(input,key,destination);store.Journal("Сейф","Выполнено","Содержимое контейнера восстановлено без перезаписи существующих файлов.");return result;
                }
            }
            finally { Clear(key); }
        }
        internal static void Seal(Stream input,string target,byte[] key,byte[] salt,byte[] iv,int iterations,Dictionary<string,object> metadata)
        {
            string temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";bool moved=false;
            try
            {
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
                {
                    using(var writer=new BinaryWriter(output,Encoding.UTF8,true)){writer.Write(Magic);writer.Write(iterations);writer.Write(salt);writer.Write(iv);}
                    using(var aes=Aes.Create())
                    {
                        aes.Key=key.Take(32).ToArray();aes.IV=iv;aes.Mode=CipherMode.CBC;aes.Padding=PaddingMode.PKCS7;
                        using(var crypto=new CryptoStream(output,aes.CreateEncryptor(),CryptoStreamMode.Write,true))
                        {
                            byte[] encoded=Store.Utf8.GetBytes(Store.Json(metadata));
                            byte[] length=BitConverter.GetBytes(encoded.Length);crypto.Write(length,0,length.Length);crypto.Write(encoded,0,encoded.Length);input.CopyTo(crypto,65536);crypto.FlushFinalBlock();
                        }
                    }
                    output.Flush(true);long authenticatedLength=output.Length;output.Position=0;
                    using(var hmac=new HMACSHA256(key.Skip(32).Take(32).ToArray()))
                    { byte[] mac=HashRange(output,hmac,authenticatedLength);output.Position=authenticatedLength;output.Write(mac,0,mac.Length); }
                    output.Flush(true);
                }
                File.Move(temporary,target);moved=true;
            }
            finally {if(!moved&&File.Exists(temporary))File.Delete(temporary);}
        }
        private sealed class Header {internal int Iterations;internal byte[] Salt,IV;}
        private static Header ReadHeader(Stream input)
        {
            if(input.Length<HeaderLength+16+32||input.Length>1024L*1024*1024*1024)throw new InvalidDataException("Размер контейнера некорректен.");input.Position=0;
            using(var reader=new BinaryReader(input,Encoding.UTF8,true))
            {
                if(!Equal(reader.ReadBytes(8),Magic))throw new InvalidDataException("Неизвестный формат сейфа.");
                int iterations=reader.ReadInt32();if(iterations<100000||iterations>2000000)throw new InvalidDataException("Недопустимые параметры контейнера.");
                return new Header{Iterations=iterations,Salt=reader.ReadBytes(32),IV=reader.ReadBytes(16)};
            }
        }
        private static byte[] HashRange(Stream input,HashAlgorithm hash,long count)
        {
            byte[] buffer=new byte[65536];while(count>0){int read=input.Read(buffer,0,(int)Math.Min(buffer.Length,count));if(read<=0)throw new EndOfStreamException();hash.TransformBlock(buffer,0,read,buffer,0);count-=read;}hash.TransformFinalBlock(new byte[0],0,0);return hash.Hash;
        }
        internal static void Verify(Stream input,byte[] key)
        {
            ReadHeader(input);input.Position=0;byte[] actual;
            using(var hmac=new HMACSHA256(key.Skip(32).Take(32).ToArray()))actual=HashRange(input,hmac,input.Length-32);
            var expected=new byte[32];if(input.Read(expected,0,32)!=32||!Equal(actual,expected))throw new CryptographicException("Неверный пароль или контейнер повреждён. Данные не восстановлены.");
        }
        internal static object Unseal(Stream input,byte[] key,string destination)
        {
            var header=ReadHeader(input); Verify(input,key); input.Position=HeaderLength;
            string temporary=Path.Combine(destination,".pcguard-restore-"+Guid.NewGuid().ToString("N")+".tmp");bool moved=false;
            try
            {
                Dictionary<string,object> metadata;string hash;long count=0;
                using(var aes=Aes.Create())
                {
                    aes.Key=key.Take(32).ToArray();aes.IV=header.IV;aes.Mode=CipherMode.CBC;aes.Padding=PaddingMode.PKCS7;
                    using(var bounded=new BoundedStream(input,input.Length-HeaderLength-32))
                    using(var crypto=new CryptoStream(bounded,aes.CreateDecryptor(),CryptoStreamMode.Read))
                    using(var reader=new BinaryReader(crypto,Encoding.UTF8,true))
                    {
                        int size=reader.ReadInt32();if(size<2||size>16384)throw new InvalidDataException("Некорректные метаданные сейфа.");
                        byte[] data=reader.ReadBytes(size);if(data.Length!=size)throw new EndOfStreamException();metadata=Store.Object(Store.Serializer().DeserializeObject(Store.Utf8.GetString(data)));
                        string name=Store.Text(metadata,"name");if(name!=Path.GetFileName(name)||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||name.Length==0||name=="."||name=="..")throw new InvalidDataException("Недопустимое имя файла в сейфе.");
                        string target=SafeFiles.Full(Path.Combine(destination,name));if(File.Exists(target)||Directory.Exists(target))throw new IOException("Файл с таким именем уже существует. Он не перезаписан.");
                        SafeFiles.CreateParent(temporary);
                        using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        using(var sha=SHA256.Create())
                        {
                            byte[] buffer=new byte[65536];int read;while((read=crypto.Read(buffer,0,buffer.Length))>0){output.Write(buffer,0,read);sha.TransformBlock(buffer,0,read,buffer,0);count+=read;}sha.TransformFinalBlock(new byte[0],0,0);hash=BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant();output.Flush(true);
                        }
                        if(count!=Convert.ToInt64(Store.Get(metadata,"size"))||hash!=Store.Text(metadata,"sha256"))throw new InvalidDataException("Содержимое не совпало с метаданными сейфа.");
                    }
                }
                string final=SafeFiles.Full(Path.Combine(destination,Store.Text(metadata,"name")));SafeFiles.CreateParent(final);File.Move(temporary,final);moved=true;return Store.Map("path",final,"size",count,"sha256",hash);
            }
            finally{if(!moved&&File.Exists(temporary))File.Delete(temporary);}
        }
        // CryptoStream получает только шифротекст; MAC никогда не попадает в дешифратор.
        private sealed class BoundedStream:Stream
        {
            private readonly Stream source;private long remaining;
            internal BoundedStream(Stream source,long length){this.source=source;remaining=length;}
            public override int Read(byte[] buffer,int offset,int count){if(remaining==0)return 0;int read=source.Read(buffer,offset,(int)Math.Min(count,remaining));remaining-=read;return read;}
            public override bool CanRead{get{return true;}}public override bool CanSeek{get{return false;}}public override bool CanWrite{get{return false;}}
            public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override void Flush(){}public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long value){throw new NotSupportedException();}public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}
        }
    }
}
