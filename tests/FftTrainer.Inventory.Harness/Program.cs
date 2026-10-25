using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FftTrainer.App;
using FftTrainer.Core;

namespace FftTrainer.Inventory.Harness;

// Run in an interactive Windows desktop (not a service/SSH-only session).
// dotnet run -c Release --project tests/FftTrainer.Inventory.Harness -- --output D:\fft-items-results
// Add --baseline-layout to reconstruct the old unvirtualized hierarchy using the
// SAME real row template. This compares layout only, not old ViewModel performance.
internal static class Program
{
    private sealed class NoGameSession : IGameSession
    {
        public IReadOnlyList<WriteResult> Write(long generation, IReadOnlyList<MemoryEdit> edits) =>
            throw new InvalidOperationException("Harness must never write to a game.");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--hack-ui") || args.Contains("--learn-ui"))
            return HackCheck.Run(args);
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex >= 0 && outputIndex + 1 < args.Length
            ? Path.GetFullPath(args[outputIndex + 1])
            : Path.Combine(Path.GetTempPath(), "fft-inventory-harness");
        Directory.CreateDirectory(output);
        var baseline = args.Contains("--baseline-layout");
        var mode = baseline ? "baseline-layout" : "recycling";
        // Deliberately NOT FftTrainer.App.App: its startup acquires a mutex and opens
        // a production window. Only load its theme; internal ctor injects a fake VM.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/FftTrainer.App;component/Theme.xaml", UriKind.Relative)
        });
        var text = new GameTextCatalog();
        text.Parse("items", JsonSerializer.Serialize(Enumerable.Range(0, FftLayout.InventoryCount)
            .Select(id => new { id, name = $"Harness item {id:D3}", description = $"Description {id:D3}\nSecond line" })));
        var rootIndex = Array.IndexOf(args, "--game-root");
        if (rootIndex >= 0)
        {
            Check(rootIndex + 1 < args.Length, "game root argument provided");
            var root = args[rootIndex + 1];
            var service = new GameTextService(text, Path.Combine(output, "text-cache"));
            service.SetGameRoot(root);
            service.Load(Localizer.Instance.Language);
            var stats = new EquipmentStatsReader().Load(root);
            Check(stats != null, "installed executable matches the verified equipment build");
            Check(stats![1].WeaponPower == 3 && stats[1].WeaponEvade == 5, "real dagger base values");
            Check(stats[2].WeaponPower == 4 && stats[2].WeaponEvade == 5, "real mythril knife base values");
            Check(stats[19].WeaponPower == 4 && stats[19].WeaponEvade == 5, "real broadsword base values");
            Check(stats[157].Hp == 8 && stats[186].Hp == 5 && stats[208].Move == 1, "real armor and boots bonuses");
            Check(text.ItemDescription(19).Length > 0 && text.ItemDescription(257).Contains('\n'), "real localized descriptions and bonus equipment newlines");
            Check(!stats.ContainsKey(257), "unverified bonus equipment is not assigned numerical stats");
            File.WriteAllText(Path.Combine(output, mode + "-equipment.json"), JsonSerializer.Serialize(new
            {
                Stats = new[] { 1, 2, 19, 157, 186, 208 }.ToDictionary(id => id, id => stats[id]),
                Descriptions = new[] { 19, 208, 257 }.ToDictionary(id => id, text.ItemDescription)
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (args.Contains("--data-only"))
        {
            Check(rootIndex >= 0, "data-only mode requires an explicit installed game root");
            Console.WriteLine("PASS: installed equipment values and descriptions verified; no window or game session opened.");
            return 0;
        }
        var model = new TrainerViewModel(new NoGameSession(), text: text) { Tab = 2, ShowAllItems = true };
        var snapshot = new GameSnapshot(1, 0, [], Enumerable.Repeat((byte)1, FftLayout.InventoryCount).ToArray(),
            Poaching: Enumerable.Repeat((byte)1, FftLayout.PoachingCount).ToArray());
        model.ApplySnapshot(snapshot);
        var window = new MainWindow(model);
        model.Tab = 2; // XAML's initially checked roster tab runs during InitializeComponent.
        window.Loaded += async (_, _) =>
        {
            try
            {
                await CheckWindow(window, model, snapshot, baseline, output, mode);
                Console.WriteLine("PASS: no game session was created; no game writes occurred.");
                window.Close();
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                File.WriteAllText(Path.Combine(output, mode + "-failure.txt"), ex.ToString());
                window.Close();
                app.Shutdown(1);
            }
        };
        return app.Run(window);
    }

    private static async Task CheckWindow(MainWindow window, TrainerViewModel model, GameSnapshot snapshot,
        bool baseline, string output, string mode)
    {
        Descendants<RadioButton>(window).Single(button => Equals(button.Tag, "2")).IsChecked = true;
        await Settle(window);
        var list = (ItemsControl)window.FindName("InventoryList");
        if (baseline)
        {
            // Exact old structure: bounded outer ScrollViewer measures the ordinary
            // ItemsControl/StackPanel with infinite available height.
            var parent = (Grid)list.Parent;
            var oldList = new ItemsControl { ItemTemplate = list.ItemTemplate, ItemsSource = model.VisibleItems, Resources = list.Resources };
            var outer = new ScrollViewer { Content = oldList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(outer, Grid.GetRow(list));
            parent.Children.Remove(list);
            parent.Children.Add(outer);
            list = oldList;
        }
        await Settle(window);
        var scroll = baseline ? (ScrollViewer)list.Parent
            : (ScrollViewer)list.Template.FindName("InventoryScrollHost", list);
        var firstContainers = Containers(list);
        var topCount = firstContainers.Count;
        var rowHeight = ((FrameworkElement)firstContainers.First().Key).ActualHeight;
        var viewportRows = (int)Math.Ceiling(scroll.ViewportHeight / rowHeight);
        var bound = 4 * viewportRows + 4; // viewport + one page each side + rounding/focused row
        var maxRealized = topCount;
        var seen = new Dictionary<DependencyObject, object>(firstContainers);
        var reused = new HashSet<DependencyObject>();
        Check(rowHeight > 0 && scroll.ViewportHeight > 0 && scroll.ScrollableHeight > 0, "finite scrollable viewport");
        if (!baseline)
        {
            Check(Descendants<VirtualizingStackPanel>(list).Any(), "actual virtualizing items host");
            Check(topCount <= bound && topCount < model.Items.Count / 2, "only viewport-near containers created");
        }
        else
            Check(topCount == model.Items.Count, "baseline materializes all rows");
        CheckInventoryRows(list);
        Screenshot(window, Path.Combine(output, mode + "-top.png"));
        window.Activate();
        var detailName = Descendants<TextBlock>(list).First(block => block.Name == "InventoryItemName"
            && block.DataContext is InventoryItemViewModel { Id: 1, IsPoaching: false });
        ToolTipService.SetInitialShowDelay(detailName, 0);
        var hoverPoint = detailName.PointToScreen(new Point(Math.Min(10, detailName.ActualWidth / 2), detailName.ActualHeight / 2));
        SetCursorPos((int)hoverPoint.X, (int)hoverPoint.Y);
        await Task.Delay(1000);
        await Settle(window);
        var tooltip = PresentationSource.CurrentSources.Cast<PresentationSource>()
            .Where(source => source.RootVisual != null)
            .SelectMany(source => Descendants<ToolTip>(source.RootVisual))
            .FirstOrDefault(tip => tip.IsOpen);
        Check(tooltip != null && Equals(tooltip.Content, ((InventoryItemViewModel)detailName.DataContext).DetailsText),
            "actual hover opens current item details");
        Check(tooltip!.ActualWidth <= 460, "equipment tooltip wraps within readable width");
        Screenshot(tooltip, Path.Combine(output, mode + "-tooltip.png"));
        SetCursorPos((int)hoverPoint.X, (int)hoverPoint.Y - 90);
        await Settle(window);

        // Edit actual production Stepper; current snapshot must not overwrite focus.
        var first = model.VisibleItems[0];
        var editor = Descendants<TextBox>((DependencyObject)list.ItemContainerGenerator.ContainerFromIndex(0))
            .Single(box => ReferenceEquals(box.DataContext, first.Quantity));
        window.Activate();
        Check(editor.Focus(), "focus first quantity editor (requires interactive desktop)");
        await Settle(window);
        Check(first.Quantity.IsEditing, "focus marks the original field editing");
        var updated = snapshot with
        {
            Inventory = Enumerable.Repeat((byte)2, FftLayout.InventoryCount).ToArray(),
            Poaching = Enumerable.Repeat((byte)2, FftLayout.PoachingCount).ToArray()
        };
        model.ApplySnapshot(updated);
        Check(first.Quantity.Value == 1, "focused clean value protected");
        editor.SetCurrentValue(TextBox.TextProperty, "42");
        editor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Check(first.Quantity.Value == 42 && first.Quantity.IsDirty, "actual text binding stages edit");
        await Settle(window);
        CheckInventoryRows(list);
        var dirtyContainer = (DependencyObject)list.ItemContainerGenerator.ContainerFromIndex(0);
        var dirtyRow = Descendants<Border>(dirtyContainer).Single(row => row.Name == "InventoryRow");
        var dirtyMarker = Descendants<TextBlock>(dirtyContainer).Single(block => block.Name == "InventoryQuantityChange");
        Check(dirtyMarker.IsVisible && dirtyMarker.Text == "1 → 42", "dirty row shows protected original and staged quantity beside name");
        Screenshot(window, Path.Combine(output, mode + "-dirty.png"));
        var dirtyRowRebound = false;
        scroll.ScrollToEnd();
        await Settle(window);
        Check(ReferenceEquals(editor.DataContext, first.Quantity), "focused offscreen editor never rebinds");
        var search = Descendants<TextBox>(window).Single(box =>
            box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == "ItemSearch");
        Check(search.Focus(), "move keyboard focus to search");
        await Settle(window);
        Check(!first.Quantity.IsEditing, "blur clears old field editing flag");

        // Sweep through every viewport. Identity comparison proves reuse, not just
        // a small initial count; return to both ends to flush the recycle queue.
        scroll.ScrollToHome();
        await Settle(window);
        for (var offset = 0d; offset <= scroll.ScrollableHeight + scroll.ViewportHeight;
             offset += Math.Max(1, scroll.ViewportHeight * .8))
        {
            scroll.ScrollToVerticalOffset(Math.Min(offset, scroll.ScrollableHeight));
            await Settle(window);
            var current = Containers(list);
            CheckInventoryRows(list);
            // Follow the actual formerly dirty border, not only container counts:
            // its new clean DataContext must reset both trigger and text visibility.
            if (dirtyRow.DataContext is InventoryItemViewModel rebound && !ReferenceEquals(rebound, first)
                && Descendants<Border>(list).Any(row => ReferenceEquals(row, dirtyRow)))
            {
                Check(!rebound.Quantity.IsDirty, "formerly dirty visual is rebound to a clean item");
                CheckInventoryRow(dirtyRow, rebound);
                dirtyRowRebound = true;
            }
            maxRealized = Math.Max(maxRealized, current.Count);
            foreach (var pair in current)
            {
                if (seen.TryGetValue(pair.Key, out var previous) && !ReferenceEquals(previous, pair.Value))
                    reused.Add(pair.Key);
                seen[pair.Key] = pair.Value;
            }
            if (!baseline)
                Check(current.Count <= bound, "realized count stays viewport-bounded while scrolling");
        }
        scroll.ScrollToEnd();
        await Settle(window);
        Check(list.ItemContainerGenerator.ContainerFromIndex(model.VisibleItems.Count - 1) != null, "last item reachable");
        var bottomCount = Containers(list).Count;
        Screenshot(window, Path.Combine(output, mode + "-bottom.png"));
        if (!baseline)
        {
            Check(reused.Count > 0, "containers are actually reused for different items");
            Check(dirtyRowRebound, "formerly dirty row visual is recycled without inheriting highlight or marker");
        }
        Check(first.Quantity.Value == 42, "dirty value survives recycling");
        Check(model.Items.Skip(1).All(item => item.Quantity.Value == 2 && !item.Quantity.IsDirty), "no edit leaks to other items");
        scroll.ScrollToHome();
        await Settle(window);
        CheckInventoryRows(list);
        var undoRow = Descendants<Border>((DependencyObject)list.ItemContainerGenerator.ContainerFromIndex(0))
            .Single(row => row.Name == "InventoryRow");
        Check(Descendants<TextBlock>(undoRow).Single(block => block.Name == "InventoryQuantityChange").IsVisible,
            "dirty marker returns when the edited item scrolls back into view");
        model.Undo.Execute(null);
        Check(first.Quantity.Value == 2 && model.Pending.Count == 0, "undo restores latest snapshot, not stale initial value");
        scroll.ScrollToHome();
        await Settle(window);
        var restored = Descendants<TextBox>((DependencyObject)list.ItemContainerGenerator.ContainerFromIndex(0))
            .Single(box => ReferenceEquals(box.DataContext, first.Quantity));
        Check(restored.Text == "2", "recycled editor displays reverted field");
        CheckInventoryRow(undoRow, first);
        CheckInventoryRows(list);
        Screenshot(window, Path.Combine(output, mode + "-undo.png"));

        // Filter removal and generation reset while focused exercise context cleanup,
        // not only normal scrolling. No stale IsEditing or text may migrate to a row.
        Check(restored.Focus(), "refocus before filter");
        model.ItemSearch = "0x64";
        await Settle(window);
        Check(!first.Quantity.IsEditing, "filter removal clears old editing state");
        Check(model.Items.All(item => !item.Quantity.IsDirty), "filter does not commit stale text");
        model.ItemSearch = "";
        scroll.ScrollToHome();
        await Settle(window);
        restored = Descendants<TextBox>((DependencyObject)list.ItemContainerGenerator.ContainerFromIndex(0))
            .Single(box => ReferenceEquals(box.DataContext, first.Quantity));
        Check(restored.Focus(), "refocus before reconnect");
        model.ApplySnapshot(null);
        model.ApplySnapshot(updated with { Generation = 2 });
        await Settle(window);
        Check(!first.Quantity.IsEditing, "generation replacement clears detached editor flag");
        Check(!ReferenceEquals(first, model.Items[0]) && model.Pending.Count == 0, "generation creates clean identities");
        Check(model.Items.All(item => item.Quantity.Value == 2 && !item.Quantity.IsDirty), "reconnect has no cross-row edit");

        var result = new
        {
            Mode = mode, TotalItems = model.Items.Count, ViewportHeight = scroll.ViewportHeight,
            RowHeight = rowHeight, ViewportRows = viewportRows, ContainerBound = bound,
            TopRealized = topCount, BottomRealized = bottomCount, MaximumRealized = maxRealized,
            ReusedContainerIdentities = reused.Count, DirtyRowVisualRebound = dirtyRowRebound, Passed = true
        };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        File.WriteAllText(Path.Combine(output, mode + ".json"), json);
    }

    private static void CheckInventoryRows(ItemsControl list)
    {
        var rows = Descendants<Border>(list).Where(row => row.Name == "InventoryRow").ToArray();
        Check(rows.Length > 0, "inventory row visuals are present");
        foreach (var row in rows)
        {
            Check(row.DataContext is InventoryItemViewModel, "row has current inventory context");
            CheckInventoryRow(row, (InventoryItemViewModel)row.DataContext);
        }
    }

    private static void CheckInventoryRow(Border row, InventoryItemViewModel item)
    {
        var dirty = item.Quantity.IsDirty;
        Check(ReferenceEquals(row.DataContext, item), "row context matches the expected item");
        Check(ReferenceEquals(row.Background, row.FindResource(dirty ? "GoldBg" : "Panel")),
            "whole row background follows current quantity dirty state");
        Check(ReferenceEquals(row.BorderBrush, row.FindResource(dirty ? "Gold" : "Line")),
            "whole row border follows current quantity dirty state");
        var marker = Descendants<TextBlock>(row).Single(block => block.Name == "InventoryQuantityChange");
        Check(marker.Visibility == (dirty ? Visibility.Visible : Visibility.Collapsed),
            "quantity change marker is present only on dirty rows");
        var name = Descendants<TextBlock>(row).Single(block => block.Name == "InventoryItemName");
        Check(name.Text == item.Name && Equals(name.ToolTip, item.DetailsText), "name and details tooltip follow the current item");
        var stats = Descendants<TextBlock>(row).Single(block => block.Name == "InventoryStatSummary");
        Check(stats.Text == item.StatSummary, "equipment summary follows the current item");
        Check(stats.Visibility == (string.IsNullOrEmpty(item.StatSummary) ? Visibility.Collapsed : Visibility.Visible),
            "empty equipment summary does not occupy a line");
        Check(name.TextTrimming == TextTrimming.CharacterEllipsis, "long item names are ellipsized");
        Check(ReferenceEquals(name.Parent, marker.Parent), "change marker is grouped with the name");
        if (dirty)
        {
            Check(marker.Text == $"{item.Quantity.Original} → {item.Quantity.Value}",
                "dirty marker contains original and current quantity, not color alone");
            var nameRight = name.TranslatePoint(new Point(name.ActualWidth, 0), row).X;
            var markerLeft = marker.TranslatePoint(new Point(0, 0), row).X;
            Check(markerLeft >= nameRight && markerLeft - nameRight <= 12,
                "quantity change marker stays close to the name");
        }
    }

    private static Dictionary<DependencyObject, object> Containers(ItemsControl list)
    {
        var result = new Dictionary<DependencyObject, object>();
        for (var i = 0; i < list.Items.Count; i++)
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject container)
                result.Add(container, list.Items[i]);
        return result;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private static async Task Settle(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(25);
        window.UpdateLayout();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    private static void Screenshot(FrameworkElement window, string path)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        png.Save(file);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("FAIL: " + message);
    }
}
