using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FftTrainer.App;
using FftTrainer.Core;

namespace FftTrainer.Inventory.Harness;

// --hack-ui --game-root <installed game directory> --output <results> [--language zh-Hans]
// Real production XAML/handlers and official local game text, but synthetic roster
// and rules. No process attachment, modal dialog, input injection or game writes.
internal static class HackCheck
{
    private sealed class NoGameSession : IGameSession
    {
        public int WriteAttempts { get; private set; }
        public IReadOnlyList<WriteResult> Write(long generation, IReadOnlyList<MemoryEdit> edits)
        {
            WriteAttempts++;
            throw new InvalidOperationException("Hack UI harness must never call Write.");
        }
    }

    internal static int Run(string[] args)
    {
        string? Argument(string key)
        {
            var index = Array.IndexOf(args, key);
            if (index < 0) return null;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Missing value for " + key);
            return args[index + 1];
        }

        var learningMode = args.Contains("--learn-ui");
        var mode = learningMode ? "learn-ui" : "hack-ui";
        var output = Path.GetFullPath(Argument("--output")
            ?? Path.Combine(Path.GetTempPath(), "fft-hack-ui-harness"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var screenshots = new List<string>();
        var session = new NoGameSession();
        var activations = 0;
        Application? app = null;
        MainWindow? window = null;
        GameTextCatalog? text = null;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            checks.Add(message);
        }
        void Report(Exception? error)
        {
            File.WriteAllText(Path.Combine(output, mode + "-result.json"), JsonSerializer.Serialize(new
            {
                Mode = mode,
                Passed = error == null,
                ExitCode = error == null ? 0 : 1,
                Checks = checks,
                Screenshots = screenshots,
                Language = Localizer.Instance.Language.Code,
                TextSource = "official installed game files (read only)",
                DataSource = "synthetic Ramza roster and UnitSkillRules; no live memory",
                CommandNames = new[] { 6, 38, 43, 69, 74 }.ToDictionary(id => id, id => text?.SkillGroupName(id)),
                session.WriteAttempts,
                WindowActivations = activations,
                WindowPosition = window == null ? null : new { window.Left, window.Top, window.ShowActivated },
                Error = error?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        try
        {
            var language = UiLanguages.Find(Argument("--language") ?? "zh-Hans")
                ?? throw new ArgumentException("Unsupported language.");
            Localizer.Instance.SetLanguage(language);
            var root = Argument("--game-root")
                ?? throw new ArgumentException("--hack-ui requires --game-root for official command names.");
            text = new GameTextCatalog();
            var service = new GameTextService(text, Path.Combine(output, "text-cache"));
            service.SetGameRoot(root);
            service.Load(language);
            var empty = new GameTextCatalog();
            Check(!text.IsEmpty && new[] { 6, 38, 43, 69, 74 }.All(id => text.SkillGroupName(id) != empty.SkillGroupName(id)),
                "official command names loaded, without fallback labels");
            Check(Localizer.T("ui.picker.secondaryHack") != "ui.picker.secondaryHack", "embedded UI text loaded");
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/FftTrainer.App;component/Theme.xaml", UriKind.Relative)
            });
            var data = new byte[FftLayout.UnitSize];
            data[0] = 1; // Ramza, verified story identity; this is not read from a process.
            data[FftLayout.Job] = 1;
            data[FftLayout.Level] = 12;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(FftLayout.NameId), 1);
            var rules = new UnitSkillRules(25,
                [new SkillJob(1, 0x4B, 6, [436, 460, 486], [436, 460, 486])], [6], [], true)
                { HackSecondary = [0, 6, 38, 43] };
            var model = new TrainerViewModel(session, text: text);
            var snapshot = new GameSnapshot(1, 0, [new UnitSnapshot(0, data, Skills: rules)], []);
            if (learningMode) snapshot = LearnCheck.WithCommand(snapshot, 69, 0x00F0);
            model.ApplySnapshot(snapshot);
            window = new MainWindow(model)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                Width = 1320,
                Height = 960,
                Focusable = false
            };
            // OpenSkillPicker calls Focus() internally. Block keyboard focus in this
            // harness only, and add NOACTIVATE before Show; never disturb the desktop.
            Keyboard.AddPreviewGotKeyboardFocusHandler(window, (_, e) => e.Handled = true);
            window.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(window).Handle;
                var style = GetWindowLongPtr(handle, -20).ToInt64(); // GWL_EXSTYLE
                SetWindowLongPtr(handle, -20, new IntPtr(style | 0x08000000L)); // WS_EX_NOACTIVATE
            };
            window.Activated += (_, _) => activations++;
            window.Loaded += async (_, _) =>
            {
                Exception? failure = null;
                try
                {
                    await Settle(window);
                    Check(window.Left == -20000 && window.Top == -20000 && !window.IsActive,
                        "window is offscreen, manual-positioned and inactive");
                    if (learningMode)
                    {
                        await LearnCheck.RunScenario(window, model, snapshot, text, output, screenshots, Check);
                        Check(session.WriteAttempts == 0, "learning UI never calls session Write");
                        Check(activations == 0 && !window.IsActive && !window.IsKeyboardFocusWithin
                            && GetForegroundWindow() != new WindowInteropHelper(window).Handle,
                            "learning harness never activated or acquired keyboard/foreground focus");
                        return;
                    }
                    var unit = model.Units.Single();
                    var secondary = unit.SkillSlots.Single(slot => slot.Slot == SkillSlot.Secondary);
                    var picker = (Border)window.FindName("SkillPicker");
                    var close = (Button)window.FindName("CloseSkillPickerButton");
                    Button ChangeButton(SkillSlotViewModel slot) => Descendants<Button>(window).Single(button =>
                        ReferenceEquals(button.DataContext, slot) && Equals(button.Content, Localizer.T("ui.skills.change")));
                    CheckBox HackToggle() => Descendants<CheckBox>(picker).Single(box =>
                        box.GetBindingExpression(ToggleButton.IsCheckedProperty)?.ParentBinding.Path?.Path == "SecondaryHackEnabled");
                    int[] CandidateIds() => Descendants<Button>(picker).Where(button => button.DataContext is SkillCandidate)
                        .Select(button => ((SkillCandidate)button.DataContext).Id).ToArray();
                    void Capture(string name)
                    {
                        Screenshot(window, Path.Combine(output, name));
                        screenshots.Add(name);
                    }

                    Click(ChangeButton(secondary));
                    await Settle(window);
                    var toggle = HackToggle();
                    Check(picker.IsVisible && ReferenceEquals(picker.DataContext, secondary), "actual change button opens secondary picker");
                    Check(toggle.IsVisible && toggle.IsChecked == false && !secondary.SecondaryHackEnabled,
                        "secondary Hack checkbox is visible and off by default");
                    Check(CandidateIds().SequenceEqual(new[] { 0, 6 }) && !CandidateIds().Contains(38),
                        "normal rendered candidates exclude cross-character commands");
                    Capture("hack-picker-off.png");

                    toggle.IsChecked = true;
                    toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                    await Settle(window);
                    Check(secondary.SecondaryHackEnabled && CandidateIds().Contains(38) && CandidateIds().Contains(43),
                        "real checkbox binding adds Aimed Shot 38 and Dragon 43");
                    Check(!model.HasChanges && session.WriteAttempts == 0, "enabling Hack alone does not stage or write");
                    Capture("hack-picker-on.png");
                    Click(Descendants<Button>(picker).Single(button => button.DataContext is SkillCandidate { Id: 38 }));
                    await Settle(window);
                    Check(picker.Visibility == Visibility.Collapsed && secondary.Field.Value == 38
                        && model.Pending.Contains(secondary.Field), "actual candidate button stages 38 and closes picker");
                    Check(secondary.Field.SecondaryHack != null && secondary.ValidPending && model.NeedsConfirmation,
                        "staged cross-character command carries authorization and requires dangerous-write confirmation");
                    Check(session.WriteAttempts == 0, "selection never calls session Write or opens a confirmation modal");
                    Capture("hack-staged.png");

                    Click(ChangeButton(secondary));
                    await Settle(window);
                    Check(HackToggle().IsChecked == false && !secondary.SecondaryHackEnabled && !CandidateIds().Contains(38),
                        "reopening the real picker resets Hack to off and removes 38");
                    Check(secondary.Field.Value == 38 && secondary.Field.SecondaryHack == null && !secondary.ValidPending,
                        "reopening revokes authorization and leaves the pending cross-character edit unwritable");
                    // Exercise the VM's write guard, not MainWindow.WriteAsync (which
                    // opens a modal). The throwing fake proves rejection precedes I/O.
                    await model.WriteAsync(dangerConfirmed: true);
                    Check(session.WriteAttempts == 0 && model.Pending.Contains(secondary.Field),
                        "revoked pending command is rejected before the fake session Write");
                    Capture("hack-picker-reopened.png");
                    Click(close);
                    await Settle(window);
                    Check(picker.Visibility == Visibility.Collapsed, "actual cancel button closes picker");

                    foreach (var slot in unit.SkillSlots.Where(slot => slot.Slot != SkillSlot.Secondary))
                    {
                        Click(ChangeButton(slot));
                        await Settle(window);
                        Check(picker.IsVisible && ReferenceEquals(picker.DataContext, slot), "actual change button opens " + slot.Slot);
                        Check(!HackToggle().IsVisible && !slot.SecondaryHackEnabled, "Hack checkbox is absent for " + slot.Slot);
                        Check(!CandidateIds().Contains(38) && !CandidateIds().Contains(43), "cross-character commands absent for " + slot.Slot);
                        if (slot.Slot == SkillSlot.Reaction) Capture("hack-reaction-picker.png");
                        Click(close);
                        await Settle(window);
                    }
                    Check(session.WriteAttempts == 0, "no session Write was called throughout the scenario");
                    Check(activations == 0 && !window.IsActive && !window.IsKeyboardFocusWithin
                        && GetForegroundWindow() != new WindowInteropHelper(window).Handle,
                        "harness never activated or acquired keyboard/foreground focus");
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    Report(failure);
                    Console.WriteLine(failure == null ? "PASS: " + mode + "; no game writes or focus activation."
                        : "FAIL: see " + mode + "-result.json");
                    window.Close();
                    app.Shutdown(failure == null ? 0 : 1);
                }
            };
            return app.Run(window);
        }
        catch (Exception ex)
        {
            Report(ex);
            Console.Error.WriteLine("FAIL: see " + mode + "-result.json");
            window?.Close();
            app?.Shutdown(1);
            return 1;
        }
    }

    internal static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    internal static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    internal static async Task Settle(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(25);
        window.UpdateLayout();
    }

    internal static void Screenshot(FrameworkElement visual, string path)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        png.Save(file);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
