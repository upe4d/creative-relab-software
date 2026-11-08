using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
namespace PCCare
{
    public sealed partial class MainController
    {
        // Только заранее заданные HTTPS-ссылки, без скрытой установки других продуктов.
        private void InitializeFamily()
        {
            Get<Button>("OpenGuardFamily").Click += delegate { OpenFamily("https://tools.crrl.ru/software/#pc-guard"); };
            Get<Button>("OpenTuneFamily").Click += delegate { OpenFamily("https://tools.crrl.ru/software/#pc-tune"); };
            Get<Button>("OpenFamilyCatalog").Click += delegate { OpenFamily("https://tools.crrl.ru/software/"); };
            Get<Button>("OpenFamilyCreator").Click += delegate { OpenFamily("https://t.me/creativerelab"); };
        }
        private void OpenFamily(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(window, "Не удалось открыть ссылку: " + ex.Message, "Creative Relab"); }
        }
    }
}