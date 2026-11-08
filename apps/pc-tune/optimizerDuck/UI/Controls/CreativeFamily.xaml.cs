using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using optimizerDuck.Services.Configuration;

namespace optimizerDuck.UI.Controls;

/// <summary>Каталог семейства Creative Relab, без фоновой загрузки и скрытой установки.</summary>
public partial class CreativeFamily : UserControl
{
    public CreativeFamily()
    {
        InitializeComponent();
    }

    // Открываем отдельный переносимый центр; оптимизации Windows здесь не выполняются.
    private void OpenUpdates(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var helper = Path.Combine(folder, "Creative Relab Updates.exe");
            if (!File.Exists(helper))
                throw new IOException(Loc.Instance["Updates.HelperMissing"]);
            Process.Start(
                new ProcessStartInfo(helper, "--product pc-tune --target \"" + folder + "\"")
                {
                    UseShellExecute = true,
                }
            );
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or global::System.ComponentModel.Win32Exception
                        or InvalidOperationException
            )
        {
            MessageBox.Show(
                Window.GetWindow(this),
                Loc.Instance["Updates.OpenFailed"] + " " + ex.Message,
                "PC Tune"
            );
        }
    }

    private void OpenProduct(object sender, RoutedEventArgs e)
    {
        var product = (sender as Button)?.Tag as string;
        var url = product switch
        {
            "pc-care" => "https://tools.crrl.ru/software/#pc-care",
            "pc-guard" => "https://tools.crrl.ru/software/#pc-guard",
            "catalog" => "https://tools.crrl.ru/software/",
            "author" => "https://t.me/creativerelab",
            _ => null,
        };
        if (url == null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
            when (ex is global::System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                Loc.Instance["Partner.OpenFailed"],
                "Creative Relab"
            );
        }
    }
}
