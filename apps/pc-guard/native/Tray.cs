using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PCGuard
{
    internal static class Notifications
    {
        internal static event Action<string> Alert;
        internal static void Send(string text){var handler=Alert;if(handler!=null)handler(text);}
    }
    // Значок показывает, что процесс работает в фоне; состояние защиты открывается отдельно.
    internal sealed class Tray:IDisposable
    {
        private readonly Thread thread;private readonly ManualResetEvent ready=new ManualResetEvent(false);
        private Control control;private NotifyIcon icon;private ApplicationContext context;
        private Exception initializationError;
        internal bool Initialized{get{return ready.WaitOne(0)&&initializationError==null&&icon!=null;}}
        internal Tray(Action open,Action stop)
        {
            thread=new Thread(delegate()
            {
                try{
                context=new ApplicationContext();control=new Control();control.CreateControl();GC.KeepAlive(control.Handle);
                icon=new NotifyIcon{Icon=Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location),Text="PC Guard: открыть состояние",Visible=true};
                var menu=new ContextMenuStrip();menu.Items.Add("Открыть PC Guard",null,delegate{open();});
                menu.Items.Add("Остановить PC Guard",null,delegate{if(MessageBox.Show("Фоновая проверка и блокировки документов остановятся. Продолжить?","PC Guard",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)==DialogResult.OK)try{stop();}catch(InvalidOperationException error){MessageBox.Show(error.Message,"PC Guard");}});
                icon.ContextMenuStrip=menu;icon.DoubleClick+=delegate{open();};Notifications.Alert+=Show;ready.Set();
                try{Application.Run(context);}finally{Notifications.Alert-=Show;icon.Visible=false;icon.Dispose();menu.Dispose();control.Dispose();}
                }catch(Exception error){initializationError=error;ready.Set();}
            });thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();if(!ready.WaitOne(5000)||initializationError!=null)throw new System.IO.IOException("Значок PC Guard не запустился.",initializationError);
        }
        private void Show(string message)
        {
            try{if(control!=null&&!control.IsDisposed)control.BeginInvoke((Action)delegate{if(icon!=null){icon.BalloonTipTitle="PC Guard";icon.BalloonTipText=message.Length>240?message.Substring(0,240):message;icon.ShowBalloonTip(6000);}});}catch(InvalidOperationException){}
        }
        public void Dispose(){try{if(control!=null&&!control.IsDisposed)control.BeginInvoke((Action)delegate{context.ExitThread();});}catch(InvalidOperationException){}thread.Join(2000);ready.Dispose();}
    }
}
