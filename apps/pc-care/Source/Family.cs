using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace PCCare
{
    public sealed partial class MainController
    {
        // Только заранее заданные HTTPS-ссылки, без скрытой установки других продуктов.
        private void InitializeFamily()
        {
            Get<Button>("OpenProductUpdates").Click += delegate { OpenUpdates(); };
            Get<Button>("OpenGuardFamily").Click += delegate { OpenFamily("https://tools.crrl.ru/software/#pc-guard"); };
            Get<Button>("OpenTuneFamily").Click += delegate { OpenFamily("https://tools.crrl.ru/software/#pc-tune"); };
            Get<Button>("OpenFamilyCatalog").Click += delegate { OpenFamily("https://tools.crrl.ru/software/"); };
            Get<Button>("OpenFamilyCreator").Click += delegate { OpenFamily("https://t.me/creativerelab"); };
        }
        // Центр обновлений только проверяет GitHub и скачивает проверенный распаковщик на D.
        private void OpenUpdates()
        {
            try {
                string folder = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                string helper = Path.Combine(folder, "Creative Relab Updates.exe");
                if (!File.Exists(helper)) throw new IOException("Не найден центр обновлений. Распакуйте программу целиком.");
                Process.Start(new ProcessStartInfo(helper, "--product pc-care --target \"" + folder + "\"") { UseShellExecute = true });
            } catch (Exception ex) { MessageBox.Show(window, "Не удалось открыть обновления: " + ex.Message, "PC Care"); }
        }
        private void OpenFamily(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(window, "Не удалось открыть ссылку: " + ex.Message, "Creative Relab"); }
        }
    }
}