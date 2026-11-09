using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PCCare
{
    // Владельцем системного диалога остаётся окно приложения.
    internal sealed class RegistryDialogOwner : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; private set; }
        internal RegistryDialogOwner(Window window) { Handle = new System.Windows.Interop.WindowInteropHelper(window).Handle; }
    }
    public sealed partial class MainController
    {
        private RegistryAudit registry;
        private List<RegistryFinding> registryRows = new List<RegistryFinding>();
        private void InitializeRegistry()
        {
            registry = new RegistryAudit(System.IO.Path.Combine(store.Directory, "Registry"));
            tables.Add("Registry", new TableView(Get<DataGrid>("RegistryGrid"), Get<TextBox>("RegistrySearch"), Get<ComboBox>("RegistryFilter"), Get<TextBlock>("RegistryCount"), delegate(object item)
            {
                RegistryFinding f = (RegistryFinding)item;
                bool only = Get<ComboBox>("RegistryFilter").SelectedIndex == 1;
                return (!only || f.Eligible) && TableView.Search(Get<TextBox>("RegistrySearch").Text, f.Location, f.Target, f.Category, f.Reason);
            }));
            Get<Button>("RegistryScan").Click += async delegate
            {
                await Run("Анализ реестра текущего пользователя…", async token =>
                {
                    registryRows = await Task.Run(() => registry.Scan(token)); tables["Registry"].SetItems(registryRows);
                    Get<TextBlock>("RegistryNote").Text = "Найдено: " + registryRows.Count + "; можно исправить: " + registryRows.Count(f => f.Eligible) + ". Выберите записи после просмотра причин.";
                    Status("Анализ реестра завершён");
                });
            };
            Get<Button>("RegistrySelect").Click += delegate
            {
                foreach (RegistryFinding f in tables["Registry"].View.Cast<RegistryFinding>()) f.Selected = f.Eligible;
                Get<DataGrid>("RegistryGrid").Items.Refresh();
            };
            Get<Button>("RegistryClear").Click += delegate { foreach (RegistryFinding f in registryRows) f.Selected = false; Get<DataGrid>("RegistryGrid").Items.Refresh(); };
            Get<Button>("RegistryFix").Click += async delegate
            {
                Get<DataGrid>("RegistryGrid").CommitEdit(); Get<DataGrid>("RegistryGrid").CommitEdit();
                List<RegistryFinding> selected = registryRows.Where(f => f.Selected && f.Eligible).ToList();
                if (selected.Count == 0) { Status("Сначала выберите записи в таблице"); return; }
                if (MessageBox.Show(window, "Удалить выбранные значения: " + selected.Count + "? Резервная копия будет сохранена до изменения. Разделы целиком не удаляются.", "Исправление реестра", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                await Run("Сохранение копии и исправление выбранных значений…", async token =>
                {
                    RegistryBackup b = await Task.Run(() => registry.Fix(selected, token));
                    store.Record("Реестр: исправление", b.Summary + "; копия: " + b.Path, null);
                    RegistryBackups(); registryRows = await Task.Run(() => registry.Scan(token)); tables["Registry"].SetItems(registryRows);
                    Get<TextBlock>("RegistryNote").Text = b.Summary + ". Копия: " + b.Path; Status(b.Summary);
                });
            };
            Get<Button>("RegistryReport").Click += async delegate
            {
                await Run("Создание отчёта…", async token =>
                {
                    string path = await Task.Run(() => registry.Report(registryRows));
                    Get<TextBlock>("RegistryNote").Text = "Отчёт сохранён: " + path; Status("Отчёт сохранён");
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                });
            };
            Get<Button>("RegistryRestore").Click += async delegate
            {
                RegistryBackup selected = Get<DataGrid>("RegistryBackups").SelectedItem as RegistryBackup;
                if (selected == null) { Status("Выберите резервную копию"); return; }
                if (MessageBox.Show(window, "Восстановить удалённые значения из копии от " + selected.Created + "? Новые значения других программ сохранятся.", "Откат реестра", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                await Run("Восстановление реестра…", async token =>
                {
                    RegistryBackup b = await Task.Run(() => registry.Restore(selected.Path, token));
                    int conflicts = b.Items.Count(i => i.State == "Conflict");
                    store.Record("Реестр: откат", b.Summary + "; конфликтов: " + conflicts, null);
                    RegistryBackups(); Get<TextBlock>("RegistryNote").Text = b.Summary + "; конфликтов: " + conflicts;
                    registryRows = await Task.Run(() => registry.Scan(token)); tables["Registry"].SetItems(registryRows); Status("Откат завершён; конфликтов: " + conflicts);
                });
            };
            Get<Button>("RegistryBackupRefresh").Click += delegate { RegistryBackups(); };
            tables["Registry"].SetItems(registryRows); RegistryBackups();
        }
        internal void LoadRegistryForTest(List<RegistryFinding> rows) { registryRows = rows; tables["Registry"].SetItems(rows); }
        public async Task CaptureRegistry()
        {
            registryRows = await Task.Run(() => registry.Scan(System.Threading.CancellationToken.None));
            tables["Registry"].SetItems(registryRows); SwitchPage("Registry");
            string directory = System.IO.Path.Combine(store.Directory, "Verification"); System.IO.Directory.CreateDirectory(directory);
            Get<TextBlock>("RegistryNote").Text = "Анализ завершён · находок: " + registryRows.Count + " · доступно для исправления: " + registryRows.Count(f => f.Eligible);
            if (store.Settings.Theme != "Dark") Get<Button>("ThemeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100); window.UpdateLayout(); SelfTests.SaveWindow(window, System.IO.Path.Combine(directory, "registry-dark.png"));
            window.Width = 960; window.UpdateLayout(); await Task.Delay(100);
            SelfTests.SaveWindow(window, System.IO.Path.Combine(directory, "registry-960.png"));
            Get<Button>("ThemeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            SelfTests.SaveWindow(window, System.IO.Path.Combine(directory, "registry-light.png"));
            Get<Button>("ThemeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            registry.Report(registryRows);
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "registry-read-only.json"), "{\"findings\":" + registryRows.Count + ",\"eligible\":" + registryRows.Count(f => f.Eligible) + ",\"registryChanges\":0}");
        }
        private void RegistryBackups() { Get<DataGrid>("RegistryBackups").ItemsSource = registry.Backups(); }
        private void UpdateRegistryButtons()
        {
            foreach (string name in new[] { "RegistryScan", "RegistryFix", "RegistryReport", "RegistrySelect", "RegistryClear", "RegistryRestore", "RegistryBackupRefresh" }) Get<Button>(name).IsEnabled = !busy;
            Get<DataGrid>("RegistryGrid").IsEnabled = !busy;
        }
    }
}
