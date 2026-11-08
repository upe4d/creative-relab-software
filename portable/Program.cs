using System;using System.Linq;using System.Windows.Forms;using System.Reflection;
[assembly: AssemblyCompany("Creative Relab")]
[assembly: AssemblyProduct("Creative Relab Portable")]
[assembly: AssemblyCopyright("Creative Relab · @creativerelab")]
namespace CreativeRelab.Portable { static class Program { [STAThread] static int Main(string[] args) { try { if(args.Contains("--verify-package")){Console.WriteLine(Package.Verify());return 0;}if(args.Contains("--self-test"))return Package.SelfTest();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new PortableForm());return 0;}catch(Exception ex){Console.Error.WriteLine(ex.Message);if(args.Length==0)MessageBox.Show(ex.Message,"Creative Relab");return 1;} } } }