using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using optimizerDuck.Common.Helpers;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Domain.Configuration;
using optimizerDuck.Domain.Optimizations.Models;
using optimizerDuck.Resources.Languages;
using optimizerDuck.Services.Configuration;
using optimizerDuck.UI.Controls;
using optimizerDuck.UI.Pages;
using optimizerDuck.UI.Windows;
using Wpf.Ui;
using Wpf.Ui.Appearance;

namespace optimizerDuck.Services.UI;

/// <summary>Проверяет реальные ресурсы и WPF-окно без запуска оптимизаций.</summary>
internal static class PCTuneVerification
{
    private static FrameworkElement? FindPage(DependencyObject root, Type type)
    {
        if (root is FrameworkElement element && element.GetType() == type)
            return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindPage(VisualTreeHelper.GetChild(root, index), type);
            if (found != null)
                return found;
        }
        return null;
    }

    // Диагностика отличает реальный цвет текста от незавершённого перехода страницы.
    private static TextBlock? FindOptimizationTitle(DependencyObject root)
    {
        if (
            root is TextBlock text
            && text.IsVisible
            && text.DataContext is BaseOptimization item
            && text.Text == item.Name
        )
            return text;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindOptimizationTitle(VisualTreeHelper.GetChild(root, i));
            if (found != null)
                return found;
        }
        return null;
    }

    private static double EffectiveOpacity(DependencyObject root)
    {
        double result = 1;
        for (DependencyObject? node = root; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement element)
                result *= element.Opacity;
        return result;
    }

    private static object DescribeTitle(DependencyObject root)
    {
        var text = FindOptimizationTitle(root);
        var parents = new List<object>();
        for (DependencyObject? node = text; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement element)
                parents.Add(
                    new
                    {
                        type = node.GetType().Name,
                        element.Name,
                        element.Opacity,
                        element.IsEnabled,
                        element.IsVisible,
                    }
                );
        }
        return new
        {
            text = text?.Text,
            color = text?.Foreground.ToString(),
            parents,
        };
    }

    internal static async Task RunAsync(App app, IServiceProvider services)
    {
        var results = new List<object>();
        var themeResources = new List<object>();
        var failures = 0;
        void Check(string name, bool passed)
        {
            results.Add(new { name, passed });
            if (!passed)
                failures++;
        }
        var directory = Path.Combine(Shared.RootDirectory, "Verification");
        Directory.CreateDirectory(directory);
        try
        {
            Check("Диагностический режим включён", Shared.VerificationMode);
            Check("Русский язык по умолчанию", new AppSettings().App.Language == "ru-RU");
            Check(
                "Данные рядом с приложением",
                Shared.RootDirectory == Path.Combine(AppContext.BaseDirectory, "Data")
            );
            Check("Рекламная ссылка", PartnerBanner.Destination == "https://zabivki.ru/");
            var neutral = Translations.ResourceManager.GetResourceSet(
                CultureInfo.InvariantCulture,
                true,
                true
            )!;
            var russian = Translations.ResourceManager.GetResourceSet(
                CultureInfo.GetCultureInfo("ru-RU"),
                true,
                false
            )!;
            foreach (global::System.Collections.DictionaryEntry entry in neutral)
            {
                var key = (string)entry.Key;
                var translated = russian.GetString(key);
                Check("Перевод: " + key, !string.IsNullOrWhiteSpace(translated));
                if (translated == null)
                    continue;
                var pattern = @"\{\d+(?:[^}]*)\}";
                var originalSlots = global::System
                    .Text.RegularExpressions.Regex.Matches((string)entry.Value!, pattern)
                    .Select(m => m.Value)
                    .Order()
                    .ToArray();
                var translatedSlots = global::System
                    .Text.RegularExpressions.Regex.Matches(translated, pattern)
                    .Select(m => m.Value)
                    .Order()
                    .ToArray();
                Check("Параметры: " + key, originalSlots.SequenceEqual(translatedSlots));
            }
            var count = typeof(App)
                .Assembly.GetTypes()
                .Count(t => t.GetCustomAttribute<OptimizationAttribute>() != null);
            Check("42 оптимизации в Release", count == 42);
            var window = services.GetRequiredService<MainWindow>();
            var navigation = services.GetRequiredService<INavigationService>();
            navigation.Navigate(typeof(SettingsPage));
            await Task.Delay(350);
            foreach (var theme in new[] { ApplicationTheme.Dark, ApplicationTheme.Light })
            {
                ApplicationThemeManager.Apply(theme, updateAccent: false);
                App.ApplyAccentFor(theme);
                themeResources.Add(
                    new
                    {
                        requested = theme.ToString(),
                        actual = ApplicationThemeManager.GetAppTheme().ToString(),
                        highContrast = SystemParameters.HighContrast,
                        background = app.TryFindResource("ApplicationBackgroundBrush")?.ToString(),
                        card = app.TryFindResource("CardBackgroundFillColorDefaultBrush")
                            ?.ToString(),
                        text = app.TryFindResource("TextFillColorPrimaryBrush")?.ToString(),
                    }
                );
                foreach (var width in new[] { 900, 1350 })
                {
                    window.Width = width;
                    window.Height = 800;
                    foreach (
                        var page in new[]
                        {
                            typeof(DashboardPage),
                            typeof(OptimizePage),
                            typeof(StartupManagerPage),
                            typeof(ScheduledTasksPage),
                            typeof(DiskCleanupPage),
                            typeof(SettingsPage),
                        }
                    )
                    {
                        Check($"Переход {theme}/{width}/{page.Name}", navigation.Navigate(page));
                        // Навигация WPF-UI завершает загрузку асинхронно. Проверяем фактическую страницу.
                        FrameworkElement? visiblePage = null;
                        for (var attempt = 0; attempt < 80; attempt++)
                        {
                            await Task.Delay(100);
                            visiblePage = FindPage(window, page);
                            if (
                                visiblePage != null
                                && visiblePage.IsVisible
                                && visiblePage.ActualHeight > 0
                                && window.RootNavigation.SelectedItem?.TargetPageType == page
                            )
                                break;
                        }
                        await Task.Delay(450);
                        Check(
                            $"Страница видна {theme}/{width}/{page.Name}",
                            visiblePage != null
                                && visiblePage.IsVisible
                                && window.RootNavigation.SelectedItem?.TargetPageType == page
                        );
                        if (visiblePage is Page actualPage)
                        {
                            Check(
                                $"Фон страницы {theme}/{width}/{page.Name}",
                                actualPage.Background?.ToString()
                                    == app.TryFindResource("PCTuneWindowBrush")?.ToString()
                            );
                            Check(
                                $"Цвет текста {theme}/{width}/{page.Name}",
                                actualPage.Foreground?.ToString()
                                    == app.TryFindResource("TextFillColorPrimaryBrush")?.ToString()
                            );
                        }
                        if (
                            Environment.GetEnvironmentVariable("PC_TUNE_SCROLL_BENCHMARK") == "1"
                            && theme == ApplicationTheme.Dark
                            && width == 1350
                            && page == typeof(ScheduledTasksPage)
                            && visiblePage != null
                        )
                            await ScrollBenchmark.RunAsync(
                                visiblePage,
                                Path.Combine(directory, "scroll-benchmark.json")
                            );
                        window.UpdateLayout();
                        var banner = window.PartnerAdvertisement;
                        var offset = banner.TransformToAncestor(window).Transform(new Point(0, 0));
                        Check(
                            $"Реклама в окне {theme}/{width}/{page.Name}",
                            banner.ActualWidth > 700
                                && banner.ActualHeight >= 60
                                && offset.Y + banner.ActualHeight <= window.ActualHeight + 1
                        );
                        Check($"Название {theme}/{width}/{page.Name}", window.Title == "PC Tune");
                        var immediate =
                            theme == ApplicationTheme.Dark
                            && width == 1350
                            && page == typeof(OptimizePage)
                                ? DescribeTitle(window)
                                : null;
                        for (
                            var attempt = 0;
                            attempt < 30
                                && visiblePage != null
                                && EffectiveOpacity(visiblePage) < 0.99;
                            attempt++
                        )
                            await Task.Delay(50);
                        Check(
                            $"Переход завершён {theme}/{width}/{page.Name}",
                            visiblePage != null && EffectiveOpacity(visiblePage) >= 0.99
                        );
                        if (immediate != null)
                            File.WriteAllText(
                                Path.Combine(directory, "optimization-visual-tree.json"),
                                JsonConvert.SerializeObject(
                                    new { immediate, settled = DescribeTitle(window) },
                                    Formatting.Indented
                                )
                            );
                        var bitmap = new RenderTargetBitmap(
                            (int)window.ActualWidth,
                            (int)window.ActualHeight,
                            96,
                            96,
                            PixelFormats.Pbgra32
                        );
                        bitmap.Render(window);
                        var png = new PngBitmapEncoder();
                        png.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(
                            Path.Combine(directory, $"{page.Name}-{theme}-{width}.png")
                        );
                        png.Save(stream);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Check(ex.ToString(), false);
        }
        var report = new
        {
            at = DateTimeOffset.Now,
            checks = results.Count,
            failures,
            themeResources,
            systemOptimizationsApplied = false,
            results,
        };
        File.WriteAllText(
            Path.Combine(directory, "results.json"),
            JsonConvert.SerializeObject(report, Formatting.Indented)
        );
        app.Shutdown(failures == 0 ? 0 : 1);
    }
}
