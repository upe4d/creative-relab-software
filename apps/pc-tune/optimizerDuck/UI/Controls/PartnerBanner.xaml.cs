using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using optimizerDuck.Services.Configuration;

namespace optimizerDuck.UI.Controls;

/// <summary>Локальный рекламный блок: сеть используется только после нажатия ссылки.</summary>
public partial class PartnerBanner : UserControl
{
    public const string Destination = "https://zabivki.ru/";

    public PartnerBanner()
    {
        InitializeComponent();
    }

    private void OpenPartner(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Destination) { UseShellExecute = true });
        }
        catch (Exception ex)
            when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(Window.GetWindow(this), Loc.Instance["Partner.OpenFailed"], "PC Tune");
        }
    }
}
