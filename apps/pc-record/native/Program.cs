using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Reflection;
[assembly:AssemblyTitle("PC Record")]
[assembly:AssemblyCompany("Creative Relab")]
[assembly:AssemblyProduct("PC Record")]
[assembly:AssemblyVersion("1.0.0.0")]
[assembly:AssemblyFileVersion("1.0.0.0")]
namespace PCRecord {
 internal static class Program {
  [STAThread]static int Main(string[] args){
   try{
    string root=AppDomain.CurrentDomain.BaseDirectory;var paths=new Paths(root);
    Environment.SetEnvironmentVariable("TEMP",paths.Temp);Environment.SetEnvironmentVariable("TMP",paths.Temp);
    Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
    if(args.Contains("--verify-extended"))return ExtendedVerification.Run(paths);
    if(args.Contains("--verify-display"))return DisplayVerification.Run(paths);
    if(args.Contains("--verify-release"))return ReleaseVerification.Run(paths);
    if(args.Contains("--verify-capture"))return Verification.Run(paths);
    Application.Run(new MainWindow(paths,args.Contains("--verify-shell"),args.Contains("--verify-controls"),args.Contains("--verify-gallery")));return 0;
   }catch(Exception ex){try{if(String.Equals(Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory),@"D:\",StringComparison.OrdinalIgnoreCase))File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fatal-error.txt"),ex.ToString());}catch(IOException){}MessageBox.Show(ex.Message,"PC Record");return 1;}
  }
 }
}