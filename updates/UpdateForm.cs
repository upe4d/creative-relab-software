using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
namespace CreativeRelab.Updates
{
    // Проверка и загрузка отделены от применения: основной процесс сам не закрывается.
    public sealed class UpdateForm:Form
    {
        private readonly Product product;private readonly string target;private readonly Version installed;
        private readonly Label summary=new Label(),status=new Label();private readonly ProgressBar progress=new ProgressBar();private readonly Button check=new RoundedButton(),download=new RoundedButton(),open=new RoundedButton();
        private Release release;private string downloaded;private bool busy,closeRequested;private CancellationTokenSource operation;
        public UpdateForm(Product product,string target)
        {
            this.product=product;this.target=ReleaseClient.SafeFolder(target);installed=ReleaseClient.Installed(product,target);
            Text="Обновления "+product.Name+" · Creative Relab";ClientSize=new Size(720,430);StartPosition=FormStartPosition.CenterScreen;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;Font=new Font("Segoe UI",10);BackColor=Color.FromArgb(16,20,27);ForeColor=Color.White;Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            using(var stream=typeof(UpdateForm).Assembly.GetManifestResourceStream("Relab.Avatar.png"))using(var source=Image.FromStream(stream)){var avatar=new PictureBox{Image=new Bitmap(source),Location=new Point(28,24),Size=new Size(56,56),SizeMode=PictureBoxSizeMode.Zoom};Controls.Add(avatar);Disposed+=delegate{avatar.Image.Dispose();};}
            Controls.Add(new Label{Text=product.Name,Font=new Font("Segoe UI",23,FontStyle.Bold),Location=new Point(101,21),AutoSize=true});
            Controls.Add(new Label{Text="Creative Relab  /  @creativerelab",Location=new Point(103,61),AutoSize=true,ForeColor=Color.FromArgb(164,181,205)});
            summary.SetBounds(28,106,664,62);summary.Text="Установлена "+installed.ToString(3)+"\nПапка: "+target;Controls.Add(summary);
            Controls.Add(new Label{Text="Скачивание только с GitHub Releases. Перед распаковкой закройте\nосновную программу. Данные Data сохраняет portable-распаковщик.",Location=new Point(28,172),Size=new Size(664,48),ForeColor=Color.FromArgb(174,184,201)});
            status.SetBounds(28,231,664,62);status.ForeColor=Color.FromArgb(174,184,201);Controls.Add(status);progress.SetBounds(28,306,664,9);Controls.Add(progress);
            check.Text="Проверить";check.SetBounds(28,344,142,39);check.Click+=async delegate{await Check();};Controls.Add(check);
            download.Text="Скачать обновление";download.SetBounds(183,344,222,39);download.Enabled=false;download.Click+=async delegate{await Download();};Controls.Add(download);
            open.Text="Открыть распаковщик";open.SetBounds(418,344,274,39);open.Enabled=false;open.Click+=delegate{Open();};Controls.Add(open);
            var link=new LinkLabel{Text="Все выпуски на GitHub ↗",Location=new Point(28,399),AutoSize=true,LinkColor=Color.FromArgb(155,185,255)};link.LinkClicked+=delegate{Process.Start(new ProcessStartInfo("https://github.com/"+ReleaseClient.Repository+"/releases"){UseShellExecute=true});};Controls.Add(link);
            Shown+=async delegate{await Check();};FormClosing+=delegate(object sender,FormClosingEventArgs e){if(busy){e.Cancel=true;closeRequested=true;if(operation!=null)operation.Cancel();status.Text="Отменяем запрос…";}};
        }
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);NativeChrome.Apply(Handle);}
        private async Task Check()
        {
            busy=true;operation=new CancellationTokenSource();check.Enabled=download.Enabled=open.Enabled=false;release=null;downloaded=null;progress.Style=ProgressBarStyle.Marquee;status.Text="Проверяем GitHub…";
            try{release=await ReleaseClient.Latest(product,operation.Token);summary.Text="Установлена "+installed.ToString(3)+"   ·   На GitHub "+release.Version.ToString(3)+(release.Preview?" Preview":"")+"\nПапка: "+target;status.Text=release.Version>installed?"Доступно обновление. Распаковщик будет скачан на D и проверен по SHA-256.":"Установлена актуальная версия для этого канала выпусков.";download.Enabled=release.Version>installed;}
            catch(Exception ex){status.Text="Не удалось проверить обновления: "+ex.Message;}
            finally{busy=false;operation.Dispose();operation=null;check.Enabled=true;progress.Style=ProgressBarStyle.Continuous;progress.Value=0;if(closeRequested)BeginInvoke(new Action(Close));}
        }
        private async Task Download()
        {
            busy=true;operation=new CancellationTokenSource();check.Enabled=download.Enabled=open.Enabled=false;status.Text="Скачиваем распаковщик на диск D…";var update=new Progress<int>(value=>progress.Value=Math.Max(0,Math.Min(100,value)));
            try{downloaded=await ReleaseClient.Download(release,product,value=>((IProgress<int>)update).Report(value),operation.Token);status.Text="SHA-256 совпала. Закройте "+product.Name+", затем откройте распаковщик.\n"+downloaded;open.Enabled=true;}
            catch(Exception ex){status.Text="Обновление не скачано: "+ex.Message;download.Enabled=true;}
            finally{busy=false;operation.Dispose();operation=null;check.Enabled=true;if(closeRequested)BeginInvoke(new Action(Close));}
        }
        internal static int CaptureSmoke(Product product, string target, string folder)
        {
            ReleaseClient.SafeFolder(folder);Directory.CreateDirectory(folder);int failures=0;
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var form=new UpdateForm(product,target)){
                form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.ShowInTaskbar=false;
                var start=DateTime.UtcNow;var timer=new System.Windows.Forms.Timer{Interval=150};
                timer.Tick+=delegate{
                    if(form.busy&&DateTime.UtcNow-start<TimeSpan.FromSeconds(40))return;
                    timer.Stop();var results=new List<object>();Action<bool,string> check=(ok,name)=>{results.Add(new{name,passed=ok});if(!ok)failures++;};
                    check(form.release!=null,"Реальная проверка GitHub завершена");check(form.Text.Contains(product.Name),"Верное название продукта");check(form.summary.Text.Contains(form.installed.ToString(3)),"Показана установленная версия");
                    check(form.release!=null&&form.summary.Text.Contains(form.release.Version.ToString(3)),"Показана версия GitHub");check(form.check.Enabled,"Повторная проверка доступна");check(!form.open.Enabled,"Непроверенный файл открыть нельзя");
                    check(form.Controls.OfType<Button>().All(button=>button.Right<=form.ClientSize.Width&&button.Bottom<=form.ClientSize.Height),"Кнопки помещаются в окно");
                    var boxes=form.Controls.OfType<PictureBox>().ToArray();check(boxes.Length==1&&boxes[0].Image.Width==256,"Локальный аватар загружен");
                    using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(folder,product.Slug+"-updates.png"),System.Drawing.Imaging.ImageFormat.Png);}
                    File.WriteAllText(Path.Combine(folder,product.Slug+"-updates-ui.json"),new JavaScriptSerializer().Serialize(new{checks=results.Count,failures=failures,networkUsed=true,downloadExecuted=false,results=results}));form.busy=false;form.Close();
                };
                form.Shown+=delegate{timer.Start();};Application.Run(form);timer.Dispose();
            }
            return failures==0?0:1;
        }
        private void Open()
        {
            try{if(release==null||downloaded==null||!ReleaseClient.VerifyFile(downloaded,release.SHA256,release.Bytes))throw new IOException("Файл изменился после проверки. Загрузите его заново.");Process.Start(new ProcessStartInfo(downloaded){UseShellExecute=true});Close();}
            catch(Exception ex){MessageBox.Show(this,ex.Message,"Creative Relab",MessageBoxButtons.OK,MessageBoxIcon.Information);}
        }
    }
}
