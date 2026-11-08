using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace CreativeRelab.Portable
{
    public sealed class PortableForm : Form
    {
        private readonly TextBox destination = new TextBox(); private readonly ProgressBar progress = new ProgressBar(); private readonly Label status = new Label(); private readonly Button unpack = new Button(); private readonly Button browse = new Button(); private readonly CheckBox launch = new CheckBox();
        public PortableForm()
        {
            Text = Package.Info.Name + " · Creative Relab"; ClientSize = new Size(650, 385); StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(16, 20, 27); ForeColor = Color.White;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            // Локальный аватар не требует сети и совпадает со знаком на сайте.
            using (var stream = typeof(PortableForm).Assembly.GetManifestResourceStream("Relab.Avatar.png")) { var avatar = new PictureBox { Image = new Bitmap(Image.FromStream(stream)), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(568,22), Size = new Size(54,54) }; Controls.Add(avatar); }
            var brand = new Label { Text = "CREATIVE RELAB  /  PORTABLE", ForeColor = Color.FromArgb(155,185,255), Location = new Point(28,24), AutoSize = true }; Controls.Add(brand);
            Controls.Add(new Label { Text = Package.Info.Name + " " + Package.Info.Version, Font = new Font("Segoe UI", 23, FontStyle.Bold), Location = new Point(25,55), AutoSize = true });
            Controls.Add(new Label { Text = "Распакуйте приложение в папку на D.\nПрограмма и данные будут храниться рядом.", Location = new Point(28,108), Size = new Size(590,48), ForeColor = Color.FromArgb(174,184,201) });
            destination.Text = Package.Info.DefaultFolder; destination.Location = new Point(28,176); destination.Width = 462; destination.BorderStyle = BorderStyle.FixedSingle; Controls.Add(destination);
            browse.Text = "Выбрать…"; browse.SetBounds(505,171,116,35); browse.Click += delegate { using (var dialog = new FolderBrowserDialog { SelectedPath = destination.Text, Description = "Папка приложения на диске D" }) if (dialog.ShowDialog(this) == DialogResult.OK) destination.Text = dialog.SelectedPath; }; Controls.Add(browse);
            launch.Text = "Открыть папку после распаковки"; launch.SetBounds(28,220,470,28); launch.Checked = true; Controls.Add(launch);
            progress.SetBounds(28,260,593,8); Controls.Add(progress); status.SetBounds(28,278,593,38); status.ForeColor = Color.FromArgb(174,184,201); status.Text = "Создатель: @creativerelab"; Controls.Add(status);
            unpack.Text = "Распаковать"; unpack.SetBounds(441,328,180,38); unpack.FlatStyle = FlatStyle.Flat; unpack.BackColor = Color.FromArgb(64,108,191); unpack.FlatAppearance.BorderSize = 0; unpack.Click += Unpack; Controls.Add(unpack);
            var cancel = new Button { Text = "Закрыть", Location = new Point(28,328), Size = new Size(116,38), FlatStyle = FlatStyle.Flat }; cancel.Click += delegate { Close(); }; Controls.Add(cancel);
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!unpack.Enabled) e.Cancel = true; };
        }
        private async void Unpack(object sender, EventArgs e)
        {
            try { var folder = Package.Target(destination.Text); unpack.Enabled = browse.Enabled = destination.Enabled = false; status.Text = "Проверка и распаковка файлов…"; var backup = await Task.Run(() => Package.Extract(folder, value => BeginInvoke(new Action(() => progress.Value = Math.Min(100,value))))); progress.Value = 100; status.Text = backup == null ? "Готово. Приложение находится в " + folder : "Готово. Предыдущие файлы сохранены: " + backup; if (launch.Checked) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
            catch (Exception ex) { status.Text = "Распаковка не завершена."; MessageBox.Show(this, ex.Message, "Creative Relab", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            finally { unpack.Enabled = browse.Enabled = destination.Enabled = true; }
        }
    }
}