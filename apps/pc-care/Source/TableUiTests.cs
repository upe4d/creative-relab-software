using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PCCare
{
    internal static class TableUiTests
    {
        private static void Check(bool condition, string name, List<string> checks)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            checks.Add("PASS: " + name);
        }
        private static T Descendant<T>(DependencyObject parent, Func<T, bool> match) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                T current = child as T;
                if (current != null && match(current)) return current;
                T found = Descendant(child, match); if (found != null) return found;
            }
            return null;
        }
        internal static async Task ClickHeader(DataGrid grid, string sortPath)
        {
            grid.UpdateLayout();
            DataGridColumnHeader header = Descendant<DataGridColumnHeader>(grid, h => h.Column != null && h.Column.SortMemberPath == sortPath);
            if (header == null) throw new InvalidOperationException("Header not visible: " + sortPath);
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(header);
            var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
            if (invoke != null) invoke.Invoke();
            else typeof(DataGridColumnHeader).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(header, null);
            await Task.Delay(120);
        }
        public static async Task Run(Window window, MainController controller, List<string> checks)
        {
            controller.SwitchPage("Large");
            var files = new List<Candidate> {
                new Candidate { Path = @"D:\Fixtures\small-report.bin", Bytes = 900, ModifiedUtc = new DateTime(2026, 1, 2) },
                new Candidate { Path = @"D:\Fixtures\medium-report.bin", Bytes = 20 * 1024 * 1024, ModifiedUtc = new DateTime(2026, 1, 1) },
                new Candidate { Path = @"D:\Fixtures\big-video.bin", Bytes = 2L * 1024 * 1024 * 1024, ModifiedUtc = new DateTime(2026, 1, 3) }
            };
            controller.LoadLarge(files); window.UpdateLayout();
            DataGrid large = controller.Get<DataGrid>("LargeGrid");
            await ClickHeader(large, "Bytes");
            Check(((Candidate)large.Items[0]).Bytes == 900 && large.Columns[0].SortDirection == ListSortDirection.Ascending, "Size header sorts ascending using bytes, with arrow", checks);
            await ClickHeader(large, "Bytes");
            Check(((Candidate)large.Items[0]).Bytes == 2L * 1024 * 1024 * 1024 && large.Columns[0].SortDirection == ListSortDirection.Descending, "Second header click reverses numeric size sorting", checks);
            controller.Get<TextBox>("LargeSearch").Text = "report";
            Check(large.Items.Count == 2, "Large-file text search filters paths", checks);
            controller.Get<ComboBox>("LargeSize").SelectedIndex = 1;
            Check(large.Items.Count == 0, "Size and text filters combine", checks);
            controller.Get<TextBox>("LargeSearch").Clear();
            Check(large.Items.Count == 1 && ((Candidate)large.Items[0]).Bytes >= 1073741824, "Size threshold filters numerically", checks);
            controller.Get<ComboBox>("LargeSize").SelectedIndex = 0;
            controller.LoadLarge(files.ToList());
            Check(((Candidate)large.Items[0]).Bytes == 2L * 1024 * 1024 * 1024, "Table refresh preserves chosen sorting", checks);
            await ClickHeader(large, "ModifiedUtc");
            Check(((Candidate)large.Items[0]).ModifiedUtc == new DateTime(2026, 1, 1), "Date header sorts by timestamp", checks);
            controller.SwitchPage("Startup");
            var snapshot = new StartupSnapshot();
            snapshot.Entries.Add(new StartupEntry { Id = "a", Name = "Активное приложение — тест", Command = @"D:\Fixtures\active.exe", Source = "Тест", Enabled = true, WindowsAllowed = true });
            snapshot.Entries.Add(new StartupEntry { Id = "b", Name = "Отключённое приложение — тест", Command = @"D:\Fixtures\off.exe", Source = "Тест", Enabled = false });
            snapshot.Entries.Add(new StartupEntry { Id = "c", Name = "Отключено Windows — тест", Command = @"D:\Fixtures\windows-off.exe", Source = "Тест", Enabled = true, WindowsAllowed = false });
            snapshot.Entries.Add(new StartupEntry { Id = "d", Name = "Неопределённое приложение — тест", Command = @"D:\Fixtures\unknown.exe", Source = "Тест", Enabled = true });
            controller.LoadStartup(snapshot); window.UpdateLayout();
            DataGrid startup = controller.Get<DataGrid>("StartupGrid");
            DataGridRow activeRow = (DataGridRow)startup.ItemContainerGenerator.ContainerFromIndex(0);
            DataGridRow offRow = (DataGridRow)startup.ItemContainerGenerator.ContainerFromIndex(1);
            Check(((SolidColorBrush)activeRow.Foreground).Color == ((SolidColorBrush)window.Resources["ActiveText"]).Color, "Enabled startup row is red", checks);
            Check(((SolidColorBrush)offRow.Foreground).Color == ((SolidColorBrush)window.Resources["InactiveText"]).Color, "Disabled startup row is green", checks);
            startup.SelectedIndex = 0; window.UpdateLayout();
            DataGridCell activeCell = Descendant<DataGridCell>(activeRow, c => true);
            Check(((SolidColorBrush)activeCell.Foreground).Color == ((SolidColorBrush)window.Resources["ActiveText"]).Color, "Red status remains visible in selected row", checks);
            controller.Get<ComboBox>("StartupStatus").SelectedIndex = 1;
            Check(startup.Items.Count == 1 && ((StartupEntry)startup.Items[0]).StatusKind == "Enabled", "Startup filter: enabled only", checks);
            controller.Get<ComboBox>("StartupStatus").SelectedIndex = 2;
            Check(startup.Items.Count == 2, "Startup filter includes both Windows and PC Care disabled records", checks);
            controller.Get<TextBox>("StartupSearch").Text = "windows-off";
            Check(startup.Items.Count == 1 && ((StartupEntry)startup.Items[0]).Id == "c", "Startup search combines with status filter", checks);
            controller.Get<TextBox>("StartupSearch").Clear(); controller.Get<ComboBox>("StartupStatus").SelectedIndex = 0;
            await ClickHeader(startup, "StatusOrder");
            Check(((StartupEntry)startup.Items[0]).StatusKind == "Enabled" && ((StartupEntry)startup.Items[3]).StatusKind == "Unknown", "Startup status header sorts status groups", checks);
            controller.SwitchPage("Cleanup");
            controller.Get<TextBox>("PreviewSearch").Text = "no-matching-fixture";
            Check(controller.Get<DataGrid>("PreviewGrid").Items.Count == 0 && controller.Get<TextBlock>("SelectionText").Text.Contains("вне текущего списка"), "Hidden selected cleanup files remain explicitly counted", checks);
            controller.Get<TextBox>("PreviewSearch").Clear(); controller.Get<ComboBox>("PreviewSize").SelectedIndex = 1;
            Check(controller.Get<DataGrid>("PreviewGrid").Items.Count == 0, "Cleanup preview supports minimum-size filter", checks);
            controller.Get<ComboBox>("PreviewSize").SelectedIndex = 0;
            controller.Get<TextBox>("CategorySearch").Text = "no-category";
            Check(controller.Get<DataGrid>("CategoryGrid").Items.Count == 0, "Cleanup category search filters rows", checks);
            controller.Get<TextBox>("CategorySearch").Clear(); controller.Get<DataGrid>("CategoryGrid").SelectedIndex = 0;
            double oldWidth = window.Width, oldHeight = window.Height;
            window.Width = 960; window.Height = 680; await Task.Delay(100); window.UpdateLayout();
            SelfTests.SaveWindow(window, System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "UiSmoke", "cleanup-minimum.png"));
            Check(controller.Get<DataGrid>("PreviewGrid").ActualHeight >= 78, "Cleanup preview remains usable at minimum window size (" + controller.Get<DataGrid>("PreviewGrid").ActualHeight.ToString("0") + " px)", checks);
            var cell = Descendant<DataGridCell>(controller.Get<DataGrid>("PreviewGrid"), c => c.Content is TextBlock);
            Check(cell != null && cell.Padding.Left >= 12 && cell.Padding.Right >= 12 && cell.Template != null, "Table cells apply actual left and right text insets", checks);
            Check(controller.Get<Image>("AppLogo").Source != null && window.Icon != null, "Application icon is embedded and displayed", checks);
            window.Width = oldWidth; window.Height = oldHeight; window.UpdateLayout();
        }
    }
}
