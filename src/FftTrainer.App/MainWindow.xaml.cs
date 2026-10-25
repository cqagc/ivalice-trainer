using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using FftTrainer.Core;

namespace FftTrainer.App;

public partial class MainWindow : Window
{
    private readonly TrainerViewModel model;
    private bool closing;
    private UnitViewModel? skillPickerUnit;
    private UnitViewModel? learningPickerUnit;
    private HwndSource? windowSource;

    public MainWindow()
    {
        // 语言要在建 VM 之前定下来：VM 构造时就会生成名称和提示文字
        var settings = UiSettings.Load(UiSettings.DefaultPath);
        Localizer.Instance.SetLanguage(UiLanguages.Find(settings.Language)
            ?? UiLanguages.FromCulture(System.Globalization.CultureInfo.CurrentUICulture));
        var layout = new FftLayout();
        var session = new GameSession(layout);
        // 游戏文本从玩家本机的游戏目录读（不随修改器分发）：先按 Steam 库找，连上游戏后以进程路径为准
        var text = new GameTextCatalog();
        var gameText = new GameTextService(text, GameTextService.DefaultCacheDirectory);
        gameText.SetGameRoot(GameTextService.FindSteamInstall());
        gameText.Load(Localizer.Instance.Language);
        model = new TrainerViewModel(session, layout, text, new SaveBackups(),
            BattlePreferences.DefaultPath, gameText);
        InitializeView();
        session.SnapshotChanged += OnSnapshot;
        Loaded += (_, _) => session.Start();
        Closed += (_, _) =>
        {
            session.SnapshotChanged -= OnSnapshot;
            session.Dispose();
        };
    }

    // Test-only construction uses the real XAML and handlers, but never creates or
    // starts GameSession, probes Steam, reads preferences, or creates backups.
    internal MainWindow(TrainerViewModel model)
    {
        this.model = model;
        InitializeView();
    }

    private void InitializeView()
    {
        DataContext = model;
        InitializeComponent();
        LanguageList.ItemsSource = UiLanguages.All;
        ApplyLanguage(Localizer.Instance.Language);
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(BeginEdit));
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(EndEdit));
        Closed += (_, _) =>
        {
            windowSource?.RemoveHook(WindowProc);
            foreach (var unit in model.Units) unit.ResetSkillHacks();
            closing = true;
        };
    }

    private void KeepFocusedInventoryRow(object sender, CleanUpVirtualizedItemEventArgs e)
    {
        // A focused stepper must not be rebound to another item's field. At most one
        // offscreen row is pinned until keyboard focus leaves it; the rest recycle.
        if (e.UIElement.IsKeyboardFocusWithin)
            e.Cancel = true;
    }

    private void ApplyLanguage(UiLanguage language)
    {
        Localizer.Instance.SetLanguage(language);
        // 中日韩共用汉字码位，xml:lang 决定字形；字体按语言换成该语言的系统字体
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(language.XmlLanguage);
        Resources["Sans"] = new FontFamily(language.SansFonts);
        Resources["Serif"] = new FontFamily(language.SerifFonts);
        model.RefreshLanguage();
    }

    private void OnLanguageSelected(object sender, RoutedEventArgs e)
    {
        LanguageButton.IsChecked = false;
        if ((sender as FrameworkElement)?.Tag is not UiLanguage language)
            return;
        ApplyLanguage(language);
        new UiSettings(language.Code).Save(UiSettings.DefaultPath);
    }

    // StaysOpen=False 点外面关闭时，把按钮状态同步回来
    private void OnLanguagePopupClosed(object? sender, EventArgs e) => LanguageButton.IsChecked = false;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        windowSource?.AddHook(WindowProc);
    }

    private static IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmGetMinMaxInfo = 0x0024;
        const uint monitorDefaultToNearest = 2;
        if (message != wmGetMinMaxInfo)
            return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, monitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return IntPtr.Zero;

        // Both Win32 rectangles and MINMAXINFO use device pixels; do not convert to WPF DIPs.
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        limits.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        limits.MaxSize.X = info.Work.Right - info.Work.Left;
        limits.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private void OnSnapshot(GameSnapshot? snapshot)
    {
        if (closing || Dispatcher.HasShutdownStarted)
            return;
        Dispatcher.Invoke(() =>
        {
            if (!closing)
            {
                model.ApplySnapshot(snapshot);
                if (SkillPicker.Visibility == Visibility.Visible
                    && (!model.CanEdit || model.SelectedUnit != skillPickerUnit
                        || SkillPicker.DataContext is not SkillSlotViewModel { CanChange: true }))
                    HideSkillPicker();
                if (LearningPicker.Visibility == Visibility.Visible
                    && (!model.CanEdit || model.SelectedUnit != learningPickerUnit
                        || learningPickerUnit?.Learning.Preview == null))
                    HideLearningPicker();
            }
        });
    }

    private FrameworkElement? editingElement;
    private object? editingContext;

    private void ClearEditingContext()
    {
        if (editingContext is EditableField field)
            field.IsEditing = false;
        if (editingContext is StatViewModel stat)
            stat.Raw.IsEditing = false;
        editingElement = null;
        editingContext = null;
    }

    private void BeginEdit(object sender, KeyboardFocusChangedEventArgs e)
    {
        // A removed visual may never route LostKeyboardFocus back to this window.
        ClearEditingContext();
        editingElement = e.NewFocus as FrameworkElement;
        editingContext = editingElement?.DataContext;
        if (editingContext is EditableField field)
            field.IsEditing = true;
        if (editingContext is StatViewModel stat)
            stat.Raw.IsEditing = true;
    }

    private void EndEdit(object sender, KeyboardFocusChangedEventArgs e)
    {
        // A delayed event from an unloaded editor must not clear a newer editor.
        if (!ReferenceEquals(e.OldFocus, editingElement))
            return;
        // Never commit an old editor's text into a recycled row's new item.
        var sameContext = editingElement != null
            && ReferenceEquals(editingElement.DataContext, editingContext);
        ClearEditingContext();
        if (sameContext && e.OldFocus is TextBox textBox)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }
    }

    private void ReleaseStepper(object sender)
    {
        if (!ReferenceEquals(sender, editingElement))
            return;
        // Clear our owner before clearing keyboard focus, so a resulting loss event
        // cannot push detached text into a new binding. This also covers +/- buttons.
        ClearEditingContext();
        if (sender is FrameworkElement { IsKeyboardFocusWithin: true })
            Keyboard.ClearFocus();
    }

    private void OnStepperContextChanged(object sender, DependencyPropertyChangedEventArgs e) => ReleaseStepper(sender);

    // Filtering or generation replacement can unload the focused row without a
    // usable LostKeyboardFocus/DataContextChanged event. Do not wait for either.
    private void OnStepperUnloaded(object sender, RoutedEventArgs e) => ReleaseStepper(sender);

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string value } && DataContext is TrainerViewModel viewModel)
            viewModel.Tab = int.Parse(value);
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeWindow(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    private void OpenSkillPicker(object sender, RoutedEventArgs e)
    {
        if (!model.CanEdit || sender is not Button { DataContext: SkillSlotViewModel { CanChange: true } slot })
            return;
        slot.ResetSecondaryHack();
        skillPickerUnit = model.SelectedUnit;
        SkillPicker.DataContext = slot;
        SkillPicker.Visibility = Visibility.Visible;
        CloseSkillPickerButton.Focus();
    }

    private void HideSkillPicker(bool keepSelection = false)
    {
        if (!keepSelection && SkillPicker.DataContext is SkillSlotViewModel slot)
            slot.ResetSecondaryHack();
        SkillPicker.Visibility = Visibility.Collapsed;
        SkillPicker.DataContext = null;
        skillPickerUnit = null;
    }

    private void CloseSkillPicker(object sender, RoutedEventArgs e) => HideSkillPicker();

    private void ChooseSkill(object sender, RoutedEventArgs e)
    {
        if (model.CanEdit && model.SelectedUnit == skillPickerUnit
            && SkillPicker.DataContext is SkillSlotViewModel slot
            && sender is Button { DataContext: SkillCandidate candidate } && slot.Select(candidate.Id))
            HideSkillPicker(keepSelection: true);
    }

    private void OpenLearningPicker(object sender, RoutedEventArgs e)
    {
        if (!model.CanEdit || model.SelectedUnit is not { } unit || !unit.Learning.BeginPreview()) return;
        learningPickerUnit = unit;
        LearningPicker.DataContext = unit.Learning;
        LearningPicker.Visibility = Visibility.Visible;
        CancelLearningButton.Focus();
    }

    private void HideLearningPicker(bool keepSelection = false)
    {
        if (!keepSelection) learningPickerUnit?.Learning.Reset();
        LearningPicker.Visibility = Visibility.Collapsed;
        LearningPicker.DataContext = null;
        learningPickerUnit = null;
    }

    private void CloseLearningPicker(object sender, RoutedEventArgs e) => HideLearningPicker();

    private void StageLearning(object sender, RoutedEventArgs e)
    {
        if (learningPickerUnit is { } unit && model.StageLearning(unit)) HideLearningPicker(keepSelection: true);
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (LearningPicker.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) HideLearningPicker();
            if (e.Key == Key.Escape || e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0) e.Handled = true;
            return;
        }
        if (SkillPicker.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
                HideSkillPicker();
            if (e.Key == Key.Escape || e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                e.Handled = true;
            return;
        }
        if (e.Key != Key.S || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;
        e.Handled = true;
        await WriteAsync();
    }

    private void OpenSaveBackups(object sender, RoutedEventArgs e)
    {
        try
        {
            if (model.BackupDirectory is not { } directory)
                return;
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            model.ReportBackupError(ex.Message);
        }
    }

    private async void WriteChanges(object sender, RoutedEventArgs e) => await WriteAsync();

    private async Task WriteAsync()
    {
        if (Keyboard.FocusedElement is TextBox textBox)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }
        if (!model.CanEdit || !model.HasChanges)
            return;
        var confirmationEdits = model.Pending.Select(field => field.ToEdit()).ToArray();
        var confirmed = false;
        if (model.NeedsConfirmation)
        {
            var changes = string.Join("\n", model.Pending.Where(field => field.IsDanger)
                .Select(field => field.ChangeText));
            confirmed = new DangerDialog(changes, confirmationEdits.Any(edit => edit.SecondaryHack != null),
                model.Pending.Any(field => field.IsDanger && field.Key is "Brave" or "Faith"),
                model.Pending.Any(field => field.Key == "Learn"))
                { Owner = this }.ShowDialog() == true;
            if (!confirmed)
                return;
        }
        // Modal dialogs pump snapshots. Never apply confirmation to changed edits
        // or a freshly issued Hack capability after reconnecting/changing modes.
        await model.WriteAsync(confirmed, confirmationEdits);
    }
}

internal sealed class DangerDialog : Window
{
    public DangerDialog(string changes, bool secondaryHack = false, bool departureRisk = true, bool learning = false)
    {
        Title = Localizer.T(learning ? "ui.learn.title" : secondaryHack ? "ui.secondaryHackRisk.title" : "ui.leaveRisk.title");
        Width = 500;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("Panel");
        // 对话框不在主窗口的资源树里，字体和 xml:lang 直接按当前语言设置
        var language = Localizer.Instance.Language;
        FontFamily = new FontFamily(language.SansFonts);
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(language.XmlLanguage);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 42,
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(0)
        });
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock
        {
            Text = Localizer.T(learning ? "ui.learn.title" : secondaryHack ? "ui.secondaryHackRisk.title" : "ui.leaveRisk.heading"),
            FontFamily = new FontFamily(language.SerifFonts),
            Foreground = (Brush)FindResource("Seal"),
            FontSize = 22,
            Margin = new Thickness(0, 0, 0, 18)
        });
        panel.Children.Add(new TextBlock
        {
            Text = (departureRisk ? Localizer.T("ui.leaveRisk.body") : "")
                + (secondaryHack ? (departureRisk ? "\n\n" : "") + Localizer.T("core.skill.hackWarning") : "")
                + (learning ? "\n\n" + Localizer.T("core.learn.warning") : ""),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 16)
        });
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 280,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = changes,
                Foreground = (Brush)FindResource("Gold"),
                TextWrapping = TextWrapping.Wrap
            }
        });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0)
        };
        var cancel = new Button { Content = Localizer.T("ui.leaveRisk.back"), IsCancel = true, Margin = new Thickness(0, 0, 12, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        var confirm = new Button { Content = Localizer.T("ui.leaveRisk.confirm"), Style = (Style)FindResource("Primary") };
        confirm.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        panel.Children.Add(buttons);
        Content = new Border
        {
            BorderBrush = (Brush)FindResource("Seal"),
            BorderThickness = new Thickness(1),
            Child = panel
        };
        Loaded += (_, _) => cancel.Focus();
    }
}

public sealed class PortraitConverter : IValueConverter
{
    private readonly PortraitCatalog catalog = PortraitCatalog.Load(AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    private readonly Dictionary<(PortraitIdentity Identity, string Kind), BitmapSource?> cache = [];

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not PortraitIdentity identity)
            return null;
        var kind = parameter as string ?? "face";
        var key = (identity, kind);
        if (cache.TryGetValue(key, out var cached))
            return cached;
        BitmapSource? image = null;
        var paths = catalog.Resolve(identity);
        var path = kind == "job" ? paths.JobIllustration : paths.Face;
        if (path != null)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                image = kind == "thumbnail"
                    ? new CroppedBitmap(bitmap, new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight / 2))
                    : bitmap;
                image.Freeze();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                // Missing or unreadable artwork leaves the hatch placeholder visible.
            }
        }
        cache[key] = image;
        return image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}


public sealed class MultiplierConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int multiplier && multiplier == int.Parse((string)parameter, CultureInfo.InvariantCulture);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? int.Parse((string)parameter, CultureInfo.InvariantCulture) : Binding.DoNothing;
}
