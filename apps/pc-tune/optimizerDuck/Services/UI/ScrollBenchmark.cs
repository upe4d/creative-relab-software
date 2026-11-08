using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json;

namespace optimizerDuck.Services.UI;

/// <summary>Измеряет реальный список на 1000 одинаковых строках без изменения задач Windows.</summary>
internal static class ScrollBenchmark
{
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, index)))
                yield return child;
    }

    internal static async Task RunAsync(FrameworkElement page, string output)
    {
        var items = Tree(page)
            .OfType<ItemsControl>()
            .Where(control => control.Items.Count > 0)
            .OrderByDescending(control => control.Items.Count)
            .First();
        var binding = BindingOperations.GetBindingBase(items, ItemsControl.ItemsSourceProperty);
        var source = items.ItemsSource;
        var sample = items.Items[0];
        var watch = Stopwatch.StartNew();
        var allocated = GC.GetTotalAllocatedBytes(false);
        items.ItemsSource = Enumerable.Repeat(sample, 1000).ToArray();
        items.UpdateLayout();
        var layoutMs = watch.Elapsed.TotalMilliseconds;
        await page.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        var created = Enumerable
            .Range(0, 1000)
            .Count(index => items.ItemContainerGenerator.ContainerFromIndex(index) != null);
        var viewer = Tree(items).OfType<ScrollViewer>().First();
        var samples = new List<double>();
        for (var step = 1; step <= 30; step++)
        {
            watch.Restart();
            viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * step / 30.0);
            await page.Dispatcher.InvokeAsync(
                () => items.UpdateLayout(),
                DispatcherPriority.Render
            );
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        var result = new
        {
            rows = 1000,
            createdContainers = created,
            layoutMs,
            scrollMedianMs = samples[samples.Count / 2],
            scrollP95Ms = samples[(int)Math.Ceiling(samples.Count * .95) - 1],
            allocatedBytes = GC.GetTotalAllocatedBytes(false) - allocated,
            canContentScroll = viewer.CanContentScroll,
            virtualizing = VirtualizingPanel.GetIsVirtualizing(items),
            recycling = VirtualizingPanel.GetVirtualizationMode(items).ToString(),
            physicalMouseTest = false,
        };
        File.WriteAllText(output, JsonConvert.SerializeObject(result, Formatting.Indented));
        if (binding != null)
            BindingOperations.SetBinding(items, ItemsControl.ItemsSourceProperty, binding);
        else
            items.ItemsSource = source;
        items.UpdateLayout();
    }
}
