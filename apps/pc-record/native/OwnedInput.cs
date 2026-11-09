using System;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
namespace PCRecord {
 // Брокер читает только собственные медиа-каталоги; политики Windows и антивирусов не изменяются.
 internal sealed class OwnedInput:IDisposable {
  readonly Process process;
  readonly Task<string> error;
  public Stream Stream{get{return process.StandardOutput.BaseStream;}}
  public OwnedInput(Paths paths,string file,long offset=0,long count=-1){Paths.RequireD(file);Paths.RejectLinks(file);string full=Path.GetFullPath(file);if(!new[]{paths.Records,paths.Temp,paths.Preview}.AnyPrefix(full))throw new IOException("Чтение вне собственных медиа-каталогов запрещено.");
   string shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
   process=new Process{StartInfo=new ProcessStartInfo(shell,"-NoLogo -NoProfile -NonInteractive -File "+MediaTools.Q(Path.Combine(paths.Root,"system","ReadMedia.ps1"))+" -Path "+MediaTools.Q(full)+" -Offset "+offset+" -Count "+count){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=paths.Root}};
   process.Start();error=process.StandardError.ReadToEndAsync();
  }
  public void Dispose(){try{Stream.Close();if(!process.WaitForExit(5000))process.Kill();}finally{process.Dispose();}}
  public static async Task<byte[]> ReadBytes(Paths paths,string file,int limit){if(new FileInfo(file).Length>limit)throw new IOException("Медиафайл превышает допустимый размер.");using(var reader=new OwnedInput(paths,file))using(var output=new MemoryStream()){await reader.Stream.CopyToAsync(output);reader.process.WaitForExit(5000);if(reader.process.ExitCode!=0)throw new IOException(await reader.error);if(output.Length>limit)throw new IOException("Медиафайл превышает допустимый размер.");return output.ToArray();}}
 }
 internal static class PrefixCheck {
  public static bool AnyPrefix(this string[] roots,string file){foreach(string root in roots)if(file.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return true;return false;}
 }
}