param([string]$Root='D:\Codex\work\CreativeRelab-Software\builds\pc-guard-3.5.1')
$ErrorActionPreference='Stop'
. (Join-Path $Root 'system\common.ps1')
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class GuardDialogProbe {
 public delegate bool EnumProc(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc e,IntPtr p);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder s,int n); public static string Text(IntPtr h){var s=new StringBuilder(120);GetWindowText(h,s,120);return s.ToString();}
 [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h,uint c);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 public static IntPtr Find(int id){IntPtr found=IntPtr.Zero;EnumWindows(delegate(IntPtr h,IntPtr p){uint pid;GetWindowThreadProcessId(h,out pid);var s=new StringBuilder(80);GetClassName(h,s,80);if(pid==(uint)id&&s.ToString()=="#32770"){found=h;return false;}return true;},IntPtr.Zero);return found;}
}
"@
$script:observed=New-Object Collections.Generic.List[object]
foreach($kind in @('file','folder')){
 $script:seen=$false;$script:currentKind=$kind
 $hostForm=New-Object Windows.Forms.Form;$hostForm.Text='PC Guard';$hostForm.Width=500;$hostForm.Height=250;$hostForm.TopMost=$true
 $timer=New-Object Windows.Forms.Timer;$timer.Interval=80
 $timer.Add_Tick({if($script:seen){return};$h=[GuardDialogProbe]::Find($PID);if($h-ne[IntPtr]::Zero){$owner=[GuardDialogProbe]::GetWindow($h,4);$script:observed.Add(@{kind=$script:currentKind;visibleDialogFound=$true;ownerPresent=($owner-ne[IntPtr]::Zero);dialogTitle=[GuardDialogProbe]::Text($h);ownerTitle=[GuardDialogProbe]::Text($owner);ownsTestWindow=($owner-eq$hostForm.Handle)});$script:seen=$true;[void][GuardDialogProbe]::PostMessage($h,0x10,[IntPtr]::Zero,[IntPtr]::Zero)}})
 $hostForm.Add_Shown({$hostForm.Activate();$timer.Start();try{$script:selection=Select-GuardPath @{kind=$script:currentKind}}finally{$timer.Stop();$hostForm.Close()}})
 [Windows.Forms.Application]::Run($hostForm);$timer.Dispose();$hostForm.Dispose()
 if(!$script:seen){throw ('No native dialog observed: '+$kind)}
}
$result=@{checks=$script:observed.Count;results=$script:observed.ToArray();noUserFilesSelected=$true;mode='Own temporary windows and native dialogs only'}
[IO.File]::WriteAllText((Join-Path $Root 'Data\Tests\guard351-dialogs.json'),($result|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
$result|ConvertTo-Json -Depth 5