using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Web.Script.Serialization;
[assembly:AssemblyCompany("Creative Relab")]
[assembly:AssemblyProduct("Creative Relab Updates")]
[assembly:AssemblyVersion("1.0.0.0")]
namespace CreativeRelab.Updates
{
    internal static class Program
    {
        private static string Argument(string[] args,string key){int index=Array.IndexOf(args,key);if(index<0||index+1>=args.Length)throw new ArgumentException("Не указан "+key);return args[index+1];}
        [STAThread]private static int Main(string[] args)
        {
            try{
                if(Array.IndexOf(args,"--self-test")>=0)return UpdateTests.Run();
                var product=Product.Get(Argument(args,"--product"));var target=ReleaseClient.SafeFolder(Argument(args,"--target"));
                if(Array.IndexOf(args,"--ui-smoke")>=0)return UpdateForm.CaptureSmoke(product,target,Argument(args,"--folder"));
                if(Array.IndexOf(args,"--check")>=0||Array.IndexOf(args,"--download-check")>=0){var installed=ReleaseClient.Installed(product,target);var release=ReleaseClient.Latest(product).GetAwaiter().GetResult();string downloaded=null;if(Array.IndexOf(args,"--download-check")>=0)downloaded=ReleaseClient.Download(release,product,delegate(int percent){}).GetAwaiter().GetResult();var report=Argument(args,"--report");ReleaseClient.SafeFolder(Path.GetDirectoryName(Path.GetFullPath(report)));var data=new{product=product.Name,installedVersion=installed.ToString(3),latestVersion=release.Version.ToString(3),updateAvailable=release.Version>installed,preview=release.Preview,releaseUrl=release.PageUrl,sha256=release.SHA256,bytes=release.Bytes,downloadedFile=downloaded,programInstalled=false};File.WriteAllText(report,new JavaScriptSerializer().Serialize(data));return 0;}
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new UpdateForm(product,target));return 0;
            }catch(Exception ex){if(Array.IndexOf(args,"--check")<0&&Array.IndexOf(args,"--download-check")<0&&Array.IndexOf(args,"--self-test")<0)MessageBox.Show(ex.Message,"Обновления Creative Relab");Console.Error.WriteLine(ex.Message);return 1;}
        }
    }
}
