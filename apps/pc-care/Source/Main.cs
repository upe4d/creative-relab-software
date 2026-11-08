using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PCCare
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
            Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
            try
            {
                if (args.Contains("--self-test")) return SelfTests.Run(directory);
                bool smoke = args.Contains("--ui-smoke");
                bool capture = args.Contains("--capture-preview");
                using (var mutex = new Mutex(false, @"Local\PCCare_" + Format.Id(directory).Substring(0, 24)))
                {
                    bool acquired;
                    try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) { if (!smoke) MessageBox.Show("PC Care уже запущен из этой папки.", "PC Care"); return 2; }
                    try
                    {
                        var store = new AppStore(Path.Combine(directory, smoke ? @"Data\UiSmoke" : "Data"));
                        var application = new Application();
                        Window window;
                        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PCCare.MainWindow.xaml"))
                        {
                            if (stream == null) throw new InvalidDataException("Не найден ресурс интерфейса.");
                            window = (Window)XamlReader.Load(stream);
                        }
                        var controller = new MainController(window, store, smoke);
                        if (!smoke)
                        {
                            window.Width = Math.Min(window.Width, SystemParameters.WorkArea.Width * 0.96);
                            window.Height = Math.Min(window.Height, SystemParameters.WorkArea.Height * 0.96);
                            window.MinWidth = Math.Min(window.MinWidth, window.Width);
                            window.MinHeight = Math.Min(window.MinHeight, window.Height);
                        }
                        if (smoke || capture) { window.Left = -20000; window.Top = -20000; window.WindowStartupLocation = WindowStartupLocation.Manual; window.ShowInTaskbar = false; }
                        window.Loaded += async delegate
                        {
                            if (!smoke && !capture) return;
                            try { if (smoke) await SelfTests.UiSmoke(window, controller, store); else await SelfTests.CaptureLivePreview(window, controller, store); application.Shutdown(0); }
                            catch (Exception ex) { File.WriteAllText(Path.Combine(store.Directory, smoke ? "ui-smoke-error.txt" : "capture-error.txt"), ex.ToString()); application.Shutdown(1); }
                        };
                        application.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                        {
                            LogError(directory, e.Exception);
                            MessageBox.Show(window, "Операция завершилась ошибкой: " + e.Exception.Message + "\nПодробности: Data\\errors.log", "PC Care", MessageBoxButton.OK, MessageBoxImage.Error);
                            e.Handled = true;
                        };
                        return application.Run(window);
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception ex)
            {
                LogError(directory, ex);
                if (!args.Contains("--self-test") && !args.Contains("--ui-smoke") && !args.Contains("--capture-preview")) MessageBox.Show("Не удалось запустить PC Care: " + ex.Message + "\nПроверь доступ к папке программы на D.", "PC Care", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
        public static void LogError(string directory, Exception ex)
        {
            try { string data = Path.Combine(directory, "Data"); Directory.CreateDirectory(data); File.AppendAllText(Path.Combine(data, "errors.log"), DateTime.Now.ToString("s") + " " + ex + Environment.NewLine); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public sealed partial class MainController
    {
        private readonly Window window;
        private readonly AppStore store;
        private readonly StartupManager startup;
        private readonly CleanerEngine cleaner;
        private readonly SystemMonitor monitor = new SystemMonitor();
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly bool smoke;
        private readonly Dictionary<string, TableView> tables = new Dictionary<string, TableView>();
        private CleanupResult cleanup;
        private ICollectionView preview;
        private CancellationTokenSource cancellation;
        private bool busy, monitoring, selectionBatch, closeWhenIdle;
        private string page = "Overview";
        private readonly string[] pages = { "Overview", "Cleanup", "Startup", "Large", "History", "About" };
        public MainController(Window window, AppStore store, bool smoke)
        {
            this.window = window; this.store = store; this.smoke = smoke;
            startup = StartupManager.Default(store);
            cleaner = new CleanerEngine(store.Settings, CleanerEngine.DefaultRoots());
            using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("PCCare.AppIcon.png"))
            {
                if (icon == null) throw new InvalidDataException("Не найден значок приложения.");
                BitmapFrame image = BitmapFrame.Create(icon, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                image.Freeze(); window.Icon = image; Get<Image>("AppLogo").Source = image; Get<Image>("CaptionIcon").Source = image;
            }
            Get<TextBlock>("InstallPath").Text = AppDomain.CurrentDomain.BaseDirectory;
            Get<TextBlock>("AgeNote").Text = "TEMP и кэш старше " + store.Settings.MinimumAgeDays + " дней. Занятые файлы и ссылки пропускаются.";
            Get<TextBlock>("LargeThreshold").Text = "От " + store.Settings.LargeFileMinimumMB + " МБ";
            Get<TextBox>("LargeRootText").Text = Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory);
            foreach (string name in pages)
            {
                string target = name;
                Get<Button>("Nav" + name).Click += delegate { SwitchPage(target); };
            }
            Get<Button>("ThemeButton").Click += delegate { store.Settings.Theme = store.Settings.Theme == "Dark" ? "Light" : "Dark"; ApplyTheme(); store.SaveSettings(); };
            Get<Button>("DataButton").Click += delegate { OpenExplorer(store.Directory, false); };
            Get<Button>("CaptionMinimize").Click += delegate { SystemCommands.MinimizeWindow(window); };
            Get<Button>("CaptionMaximize").Click += delegate { if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window); else SystemCommands.MaximizeWindow(window); };
            Get<Button>("CaptionClose").Click += delegate { SystemCommands.CloseWindow(window); };
            window.StateChanged += delegate
            {
                Get<Button>("CaptionMaximize").ToolTip = window.WindowState == WindowState.Maximized ? "Восстановить размер" : "Развернуть";
                Get<System.Windows.Shapes.Path>("CaptionMaximizeGlyph").Data = Geometry.Parse(window.WindowState == WindowState.Maximized ? "M 4 7 L 4 14 L 12 14 L 12 7 Z M 7 7 L 7 4 L 15 4 L 15 11 L 12 11" : "M 4 4 L 14 4 L 14 14 L 4 14 Z");
                Get<Border>("AppFrame").BorderThickness = window.WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
            };
            Get<Button>("ScanButton").Click += async delegate { await ScanAsync(); };
            Get<Button>("OverviewScanButton").Click += async delegate { SwitchPage("Cleanup"); await ScanAsync(); };
            Get<Button>("CleanButton").Click += async delegate { await CleanAsync(); };
            Get<Button>("CancelButton").Click += delegate { if (cancellation != null) { cancellation.Cancel(); Status("Отмена запрошена. Ожидание текущей операции…"); } };
            Get<Button>("RecycleOpenButton").Click += delegate { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); };
            Get<Button>("StartupRefreshButton").Click += delegate { RefreshStartup(); };
            Get<Button>("StartupToggleButton").Click += async delegate { await ToggleStartup(); };
            Get<Button>("BrowseButton").Click += delegate
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Папка для поиска крупных файлов", ShowNewFolderButton = false })
                { if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) Get<TextBox>("LargeRootText").Text = dialog.SelectedPath; }
            };
            Get<Button>("LargeScanButton").Click += async delegate { await ScanLargeAsync(); };
            Get<Button>("LargeOpenButton").Click += delegate { Candidate file = Get<DataGrid>("LargeGrid").SelectedItem as Candidate; if (file != null) OpenExplorer(file.Path, true); };
            Get<DataGrid>("LargeGrid").MouseDoubleClick += delegate(object sender, System.Windows.Input.MouseButtonEventArgs e)
            {
                DataGridRow row = ItemsControl.ContainerFromElement(Get<DataGrid>("LargeGrid"), e.OriginalSource as DependencyObject) as DataGridRow;
                Candidate file = row == null ? null : row.Item as Candidate;
                if (file != null && !smoke) OpenExplorer(file.Path, true);
            };
            Get<Button>("HistoryRefreshButton").Click += delegate { RefreshHistory(); };
            Get<Button>("HistoryRestoreButton").Click += async delegate { await RestoreHistory(); };
            Get<DataGrid>("CategoryGrid").SelectionChanged += delegate { if (tables.ContainsKey("Preview")) tables["Preview"].Refresh(); UpdatePreviewLabel(); };
            Get<DataGrid>("StartupGrid").SelectionChanged += delegate { UpdateButtons(); };
            Get<DataGrid>("LargeGrid").SelectionChanged += delegate { UpdateButtons(); };
            Get<DataGrid>("HistoryGrid").SelectionChanged += delegate { UpdateButtons(); };
            window.Closing += delegate(object sender, CancelEventArgs e)
            {
                if (!busy) return;
                e.Cancel = true;
                if (MessageBox.Show(window, "Выполняется операция. Отменить её и закрыть программу после завершения?", "PC Care", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                { closeWhenIdle = true; cancellation.Cancel(); }
            };
            window.Closed += delegate { timer.Stop(); };
            window.SizeChanged += delegate { ((RowDefinition)window.FindName("CategoryTableHeight")).Height = new GridLength(window.ActualHeight < 760 ? 110 : 205); };
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += async delegate { await MonitorAsync(); };
            window.Loaded += async delegate { if (!smoke) { timer.Start(); await MonitorAsync(); } };
            InitializeFamily(); InitializeTables(); ApplyTheme(); SwitchPage("Overview");
        }
        private void InitializeTables()
        {
            tables.Add("Disk", new TableView(Get<DataGrid>("DiskGrid"), Get<TextBox>("DiskSearch"), null, Get<TextBlock>("DiskCount"), item => TableView.Search(Get<TextBox>("DiskSearch").Text, ((DiskSample)item).Name)));
            tables.Add("Category", new TableView(Get<DataGrid>("CategoryGrid"), Get<TextBox>("CategorySearch"), null, Get<TextBlock>("CategoryCount"), item => TableView.Search(Get<TextBox>("CategorySearch").Text, ((Category)item).Name, ((Category)item).Note)));
            tables.Add("Preview", new TableView(Get<DataGrid>("PreviewGrid"), Get<TextBox>("PreviewSearch"), Get<ComboBox>("PreviewSize"), Get<TextBlock>("PreviewCount"), delegate(object item)
            {
                Candidate file = (Candidate)item;
                Category selected = Get<DataGrid>("CategoryGrid").SelectedItem as Category;
                return selected != null && file.CategoryId == selected.Id && file.Bytes >= TableView.Minimum(Get<ComboBox>("PreviewSize")) && TableView.Search(Get<TextBox>("PreviewSearch").Text, file.Path);
            }));
            tables["Preview"].Changed += delegate { if (cleanup != null) UpdateSelection(); };
            tables.Add("Startup", new TableView(Get<DataGrid>("StartupGrid"), Get<TextBox>("StartupSearch"), Get<ComboBox>("StartupStatus"), Get<TextBlock>("StartupCount"), delegate(object item)
            {
                StartupEntry entry = (StartupEntry)item;
                string status = Convert.ToString(Get<ComboBox>("StartupStatus").SelectedValue);
                return (string.IsNullOrEmpty(status) || status == "All" || entry.StatusKind == status) && TableView.Search(Get<TextBox>("StartupSearch").Text, entry.Name, entry.Command, entry.Source, entry.Status);
            }));
            tables.Add("Large", new TableView(Get<DataGrid>("LargeGrid"), Get<TextBox>("LargeSearch"), Get<ComboBox>("LargeSize"), Get<TextBlock>("LargeCount"), item => ((Candidate)item).Bytes >= TableView.Minimum(Get<ComboBox>("LargeSize")) && TableView.Search(Get<TextBox>("LargeSearch").Text, ((Candidate)item).Path)));
            tables.Add("History", new TableView(Get<DataGrid>("HistoryGrid"), Get<TextBox>("HistorySearch"), null, Get<TextBlock>("HistoryCount"), item => TableView.Search(Get<TextBox>("HistorySearch").Text, ((HistoryEntry)item).Time, ((HistoryEntry)item).Action, ((HistoryEntry)item).Details)));
        }
        public void LoadStartup(StartupSnapshot snapshot) { tables["Startup"].SetItems(snapshot.Entries); }
        public void LoadLarge(List<Candidate> files) { tables["Large"].SetItems(files); }
        public T Get<T>(string name) where T : FrameworkElement
        {
            T result = window.FindName(name) as T;
            if (result == null) throw new InvalidDataException("Не найден элемент интерфейса: " + name);
            return result;
        }
        public void SwitchPage(string target)
        {
            if (!pages.Contains(target)) throw new ArgumentException("Неизвестный раздел.");
            page = target;
            foreach (string name in pages)
            {
                Get<Grid>("Page" + name).Visibility = name == target ? Visibility.Visible : Visibility.Collapsed;
                Get<Button>("Nav" + name).Background = name == target ? (Brush)window.Resources["Panel"] : Brushes.Transparent;
            }
            if (target == "History") RefreshHistory();
            if (target == "Startup" && Get<DataGrid>("StartupGrid").ItemsSource == null && !busy && !smoke) RefreshStartup();
        }
        private void ApplyTheme()
        {
            bool dark = store.Settings.Theme == "Dark";
            string[] keys = { "Bg", "Panel", "Rail", "Fg", "Sub", "Line", "Soft", "Accent", "OnAccent", "ActiveText", "InactiveText", "ActiveFill", "InactiveFill" };
            string[] colors = dark ? new[] { "#101318", "#181D24", "#14181E", "#EDF0F4", "#A4AFBD", "#2B333E", "#242D39", "#92B5FF", "#101A2A", "#FF9A9F", "#75CDA9", "#2F2028", "#182A27" } : new[] { "#F6F7F9", "#FFFFFF", "#EEF0F4", "#18212C", "#526175", "#DCE1E8", "#E8EDF3", "#245ACB", "#FFFFFF", "#B42335", "#126B4A", "#FFF0F2", "#EDF8F2" };
            for (int i = 0; i < keys.Length; i++) window.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            Get<Button>("ThemeButton").Content = dark ? "Светлая тема" : "Тёмная тема";
            foreach (string name in pages) Get<Button>("Nav" + name).Background = name == page ? (Brush)window.Resources["Panel"] : Brushes.Transparent;
        }
        private void Status(string text) { Get<TextBlock>("StatusText").Text = text; Get<TextBlock>("StatusText").ToolTip = text; }
        private void UpdateButtons()
        {
            foreach (string name in new[] { "ScanButton", "OverviewScanButton", "StartupRefreshButton", "LargeScanButton", "BrowseButton", "HistoryRefreshButton" }) Get<Button>(name).IsEnabled = !busy;
            Get<DataGrid>("CategoryGrid").IsEnabled = !busy; Get<DataGrid>("PreviewGrid").IsEnabled = !busy;
            Get<Button>("CleanButton").IsEnabled = !busy && cleanup != null && (cleanup.Files.Any(f => f.Selected) || cleanup.Categories.Any(c => c.Id == "recycle" && c.Selected && c.Count > 0));
            Get<Button>("StartupToggleButton").IsEnabled = !busy && Get<DataGrid>("StartupGrid").SelectedItem != null;
            Get<Button>("LargeOpenButton").IsEnabled = !busy && Get<DataGrid>("LargeGrid").SelectedItem != null;
            HistoryEntry entry = Get<DataGrid>("HistoryGrid").SelectedItem as HistoryEntry;
            lock (store.SyncRoot) Get<Button>("HistoryRestoreButton").IsEnabled = !busy && entry != null && entry.BackupId != null && store.State.Backups.Any(b => b.Token == entry.BackupId && (b.State == "Disabled" || b.State == "Prepared"));
            Get<Button>("CancelButton").Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            Get<ProgressBar>("BusyBar").Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }
        private async Task Run(string description, Func<CancellationToken, Task> operation)
        {
            if (busy || smoke) return;
            busy = true; cancellation = new CancellationTokenSource(); UpdateButtons(); Status(description);
            try { await operation(cancellation.Token); }
            catch (OperationCanceledException) { Status("Операция отменена. Уже выполненные действия сохранены в журнале."); }
            catch (Exception ex)
            {
                Program.LogError(AppDomain.CurrentDomain.BaseDirectory, ex); Status("Ошибка: " + ex.Message);
                MessageBox.Show(window, ex.Message + "\nПодробности: Data\\errors.log", "PC Care", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                busy = false; cancellation.Dispose(); cancellation = null; UpdateButtons();
                if (page == "History") RefreshHistory();
                if (closeWhenIdle) window.Close();
            }
        }
        private async Task ScanAsync()
        {
            await Run("Проверка временных файлов…", async delegate(CancellationToken token)
            {
                CleanupResult result = await Task.Run(() => cleaner.Scan(token, true), token);
                LoadCleanup(result);
                string note = "Проверено файлов: " + result.Stats.Files + "; пропущено: " + result.Stats.Skipped + ". " + string.Join(" ", result.Notes);
                Status(note); Get<TextBlock>("FoundNote").Text = result.Stats.Limited ? "Частичная проверка: достигнут предел." : "Проверка: " + DateTime.Now.ToString("HH:mm") + " · выбери файлы перед очисткой";
            });
        }
        public void LoadCleanup(CleanupResult result)
        {
            cleanup = result;
            foreach (Category category in result.Categories) category.PropertyChanged += CategoryChanged;
            foreach (Candidate candidate in result.Files) candidate.PropertyChanged += FileChanged;
            tables["Category"].SetItems(result.Categories);
            tables["Preview"].SetItems(result.Files);
            preview = tables["Preview"].View;
            if (result.Categories.Count > 0) Get<DataGrid>("CategoryGrid").SelectedIndex = 0;
            Get<TextBlock>("FoundText").Text = Format.Bytes(result.Categories.Sum(c => c.Bytes));
            UpdatePreviewLabel(); UpdateSelection();
        }
        private void UpdatePreviewLabel()
        {
            Category category = Get<DataGrid>("CategoryGrid").SelectedItem as Category;
            Get<TextBlock>("PreviewLabel").Text = category == null ? "Файлы выбранной категории" : category.Id == "recycle" ? "Корзина очищается целиком. Содержимое — кнопка «Посмотреть корзину»." : "Файлы: " + category.Name;
        }
        private void CategoryChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "Selected" || selectionBatch) return;
            Category category = (Category)sender; selectionBatch = true;
            try { foreach (Candidate file in cleanup.Files.Where(f => f.CategoryId == category.Id)) file.Selected = category.Selected; }
            finally { selectionBatch = false; }
            UpdateSelection();
        }
        private void FileChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "Selected" || selectionBatch) return;
            Candidate file = (Candidate)sender;
            Category category = cleanup.Categories.FirstOrDefault(c => c.Id == file.CategoryId);
            selectionBatch = true;
            try { if (category != null) category.Selected = cleanup.Files.Where(f => f.CategoryId == category.Id).All(f => f.Selected); }
            finally { selectionBatch = false; }
            UpdateSelection();
        }
        private void UpdateSelection()
        {
            if (cleanup == null) return;
            long bytes = cleanup.Files.Where(f => f.Selected).Sum(f => f.Bytes);
            Category recycle = cleanup.Categories.FirstOrDefault(c => c.Id == "recycle" && c.Selected);
            if (recycle != null) bytes += recycle.Bytes;
            int hidden = preview == null ? 0 : cleanup.Files.Count(f => f.Selected && !preview.Filter(f));
            Get<TextBlock>("SelectionText").Text = "Выбрано: " + Format.Bytes(bytes) + " · " + cleanup.Files.Count(f => f.Selected) + " файлов" + (recycle != null ? " + корзина" : "") + (hidden > 0 ? " · вне текущего списка: " + hidden : "");
            UpdateButtons();
        }
        private async Task CleanAsync()
        {
            if (busy || cleanup == null) return;
            List<Candidate> files = cleanup.Files.Where(f => f.Selected).ToList();
            bool emptyBin = cleanup.Categories.Any(c => c.Id == "recycle" && c.Selected && c.Count > 0);
            if (files.Count == 0 && !emptyBin) return;
            if (files.Count > 0 && MessageBox.Show(window, "Удалить выбранные временные файлы?\n" + files.Count + " файлов · " + Format.Bytes(files.Sum(f => f.Bytes)) + "\n\nУдаление необратимо. Занятые, изменённые файлы и ссылки будут пропущены." + (emptyBin ? "\nКорзина будет подтверждена отдельно." : ""), "PC Care — Подтверждение очистки", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (emptyBin && MessageBox.Show(window, "Полностью очистить корзину всех дисков?\nФайлы из корзины будут удалены без возможности восстановления через PC Care.", "PC Care — Корзина", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) emptyBin = false;
            bool confirmedBin = emptyBin;
            await Run("Удаление выбранных файлов…", async delegate(CancellationToken token)
            {
                DeleteResult result = await Task.Run(() => cleaner.Delete(files, token));
                string summary = "Удалено: " + result.Deleted + " файлов · " + Format.Bytes(result.Bytes) + "; пропущено: " + result.Skipped + (result.Canceled ? "; отменено" : "");
                store.Record("Очистка временных файлов", summary + (result.Errors.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, result.Errors) : ""), null);
                var removed = new HashSet<string>(result.DeletedPaths, StringComparer.OrdinalIgnoreCase);
                cleanup.Files.RemoveAll(f => removed.Contains(f.Path));
                string binMessage = "";
                if (confirmedBin && !token.IsCancellationRequested)
                {
                    try { await Task.Run(() => RecycleBin.Empty()); binMessage = "; корзина очищена"; store.Record("Очистка корзины", "Корзина всех дисков", null); }
                    catch (Exception ex) { binMessage = "; корзина: " + ex.Message; store.Record("Ошибка очистки корзины", ex.Message, null); }
                }
                foreach (Category c in cleanup.Categories)
                {
                    if (c.Id == "recycle") { long bytes, count; string error; if (RecycleBin.Query(out bytes, out count, out error)) { c.Bytes = bytes; c.Count = count; } else c.Note = "Не удалось обновить: " + error; }
                    else { c.Bytes = cleanup.Files.Where(f => f.CategoryId == c.Id).Sum(f => f.Bytes); c.Count = cleanup.Files.Count(f => f.CategoryId == c.Id); }
                }
                // Rebind without attaching duplicate selection handlers to surviving rows.
                tables["Category"].Refresh(); tables["Preview"].Refresh();
                Get<TextBlock>("FoundText").Text = Format.Bytes(cleanup.Categories.Sum(c => c.Bytes));
                UpdateSelection(); Status(summary + binMessage); RefreshHistory();
            });
        }
        private async void RefreshStartup()
        {
            await Run("Чтение автозагрузки…", async delegate(CancellationToken token)
            {
                StartupSnapshot snapshot = await Task.Run(() => { lock (store.SyncRoot) return startup.List(); }, token);
                LoadStartup(snapshot);
                Status("Записей автозагрузки: " + snapshot.Entries.Count + (snapshot.Errors.Count > 0 ? ". Пропущено: " + string.Join("; ", snapshot.Errors) : ""));
            });
        }
        private async Task ToggleStartup()
        {
            StartupEntry entry = Get<DataGrid>("StartupGrid").SelectedItem as StartupEntry;
            if (entry == null || busy) return;
            if (MessageBox.Show(window, (entry.Enabled ? "Выключить автозагрузку " : "Восстановить автозагрузку ") + entry.Name + "?\n\n" + entry.Command, "PC Care", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await Run("Изменение автозагрузки…", async delegate(CancellationToken token)
            {
                StartupSnapshot snapshot = await Task.Run(() => { lock (store.SyncRoot) { if (entry.Enabled) startup.Disable(entry); else startup.Restore(entry.Id); return startup.List(); } });
                LoadStartup(snapshot);
                RefreshHistory(); Status("Автозагрузка " + entry.Name + (entry.Enabled ? " выключена. Резервная запись сохранена." : " восстановлена."));
            });
        }
        private void RefreshHistory()
        {
            lock (store.SyncRoot) tables["History"].SetItems(store.State.History.OrderByDescending(h => h.TimeUtc).ToList());
            UpdateButtons();
        }
        private async Task RestoreHistory()
        {
            HistoryEntry entry = Get<DataGrid>("HistoryGrid").SelectedItem as HistoryEntry;
            if (entry == null || entry.BackupId == null || busy) return;
            if (MessageBox.Show(window, "Восстановить исходную запись автозагрузки?\n" + entry.Details, "PC Care", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await Run("Восстановление записи…", async delegate(CancellationToken token)
            {
                await Task.Run(() => { lock (store.SyncRoot) startup.Restore(entry.BackupId); });
                Get<DataGrid>("StartupGrid").ItemsSource = null; RefreshHistory(); Status("Запись автозагрузки восстановлена.");
            });
        }
        private async Task ScanLargeAsync()
        {
            string directory;
            try { directory = Path.GetFullPath(Get<TextBox>("LargeRootText").Text); if (!Directory.Exists(directory)) throw new IOException("Папка не найдена."); }
            catch (Exception ex) { MessageBox.Show(window, "Путь для поиска: " + ex.Message, "PC Care"); return; }
            await Run("Поиск крупных файлов…", async delegate(CancellationToken token)
            {
                LargeFileResult result = await Task.Run(() => LargeFiles.Scan(directory, store.Settings, token), token);
                LoadLarge(result.Files);
                string note = "Проверено: " + result.Stats.Files + "; найдено: " + result.Matched + "; показано: " + result.Files.Count + "; пропущено: " + result.Stats.Skipped + (result.Stats.Limited ? ". Достигнут предел; результаты частичные." : ".");
                Get<TextBlock>("LargeNote").Text = note; Status(note);
                store.Record("Поиск крупных файлов", directory + " — " + note, null);
            });
        }
        private async Task MonitorAsync()
        {
            if (monitoring) return; monitoring = true;
            try
            {
                SystemSample sample = await Task.Run(() => monitor.Read());
                Get<TextBlock>("CpuText").Text = !sample.CpuAvailable ? "Недоступно" : sample.CpuPercent.HasValue ? sample.CpuPercent.Value.ToString("0.0") + " %" : "Получение данных…";
                Get<ProgressBar>("CpuBar").Value = sample.CpuPercent ?? 0;
                Get<TextBlock>("RamText").Text = sample.MemoryAvailable ? ((double)sample.UsedMemory / (1024 * 1024 * 1024)).ToString("0.0") + " / " + ((double)sample.TotalMemory / (1024 * 1024 * 1024)).ToString("0.#") + " ГБ" : "Недоступно";
                Get<ProgressBar>("RamBar").Value = sample.MemoryAvailable && sample.TotalMemory != 0 ? 100.0 * sample.UsedMemory / sample.TotalMemory : 0;
                string systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                DiskSample systemDisk = sample.Disks.FirstOrDefault(d => d.Name == systemRoot);
                Get<TextBlock>("SystemDiskLabel").Text = "Свободно на диске " + systemRoot;
                Get<TextBlock>("DiskText").Text = systemDisk == null ? "Недоступно" : systemDisk.Free;
                Get<ProgressBar>("DiskBar").Value = systemDisk == null ? 0 : 100 - systemDisk.UsedPercent;
                tables["Disk"].SetItems(sample.Disks);
            }
            catch (Exception ex) { Get<TextBlock>("CpuText").Text = "Недоступно"; if (!busy) Status("Мониторинг: " + ex.Message); }
            finally { monitoring = false; }
        }
        private void OpenExplorer(string path, bool select)
        {
            if (smoke) return;
            if ((select && !File.Exists(path)) || (!select && !Directory.Exists(path))) { MessageBox.Show(window, "Путь больше не существует: " + path, "PC Care"); return; }
            Process.Start(new ProcessStartInfo("explorer.exe", (select ? "/select," : "") + "\"" + path + "\"") { UseShellExecute = true });
        }
    }
}
