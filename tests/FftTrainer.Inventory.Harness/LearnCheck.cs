using System.IO;
using System.Windows;
using System.Windows.Controls;
using FftTrainer.App;
using FftTrainer.Core;
using static FftTrainer.Inventory.Harness.HackCheck;

namespace FftTrainer.Inventory.Harness;

// UI-only fixture, not a live-memory validation. GameSession is never constructed.
internal static class LearnCheck
{
    internal static GameSnapshot WithCommand(GameSnapshot snapshot, int group, ushort before)
    {
        int[] ids = group switch
        {
            69 => [.. Enumerable.Range(234, 14), 256],
            74 => Enumerable.Range(155, 11).ToArray(),
            6 => Enumerable.Range(368, 14).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(group))
        };
        var index = group == 6 ? 1 : 0;
        var actions = ids.Select((id, p) => new LearningAction(p, id,
            (before & ((0x80 >> (p % 8)) << (8 * (p / 8)))) != 0)).ToArray();
        var mask = (ushort)ids.Select((_, p) => (0x80 >> (p % 8)) << (8 * (p / 8))).Aggregate(0, (a, b) => a | b);
        var plan = new SkillLearningPlan(group, index, before, 0xFC, mask, "synthetic-ui-only", actions);
        var data = snapshot.Units[0].Data.ToArray();
        data[FftLayout.SecondarySkill] = (byte)group;
        data[plan.Offset] = (byte)before;
        data[plan.Offset + 1] = (byte)(before >> 8);
        data[plan.Offset + 2] = 0xFC;
        return snapshot with { Units = [snapshot.Units[0] with { Data = data,
            Skills = snapshot.Units[0].Skills! with { Learning = plan } }] };
    }

    internal static async Task RunScenario(MainWindow window, TrainerViewModel model, GameSnapshot snapshot,
        GameTextCatalog text, string output, List<string> screenshots, Action<bool, string> check)
    {
        var unit = model.Units.Single();
        var learning = unit.Learning;
        var open = (Button)window.FindName("OpenLearningButton");
        var picker = (Border)window.FindName("LearningPicker");
        var stage = (Button)window.FindName("StageLearningButton");
        var cancel = (Button)window.FindName("CancelLearningButton");
        void Capture(string name)
        {
            Screenshot(window, Path.Combine(output, name));
            screenshots.Add(name);
        }
        async Task Open()
        {
            check(open.IsEnabled, "actual learning entry enabled for the equipped command");
            Click(open);
            await Settle(window);
            check(picker.IsVisible && ReferenceEquals(picker.DataContext, learning), "actual button opens real learning preview");
        }
        check(text.HasAbility(256), "official name exists for ninth-bit action 256");
        await Open();
        check(learning.Preview is { Group: 69, Index: 0, LearnedCount: 4, AddedCount: 11 },
            "Spellblade preview reports 4 learned and 11 additions in shared k0");
        var rendered = Descendants<TextBlock>(picker).Select(block => block.Text).ToArray();
        check(rendered.Any(value => value.Contains(text.AbilityName(256)) && value.Contains("#256")),
            "real action 256 name and ID appear in the rendered list");
        check(learning.Actions.Count == 15 && learning.Actions.Count(action => !action.Learned) == 11,
            "all fifteen positions have correct learned/new labels");
        check(learning.Region.Contains("F0 00 FC → FF FE FC")
            && learning.Warning.Contains(Localizer.T("core.learn.shared")), "exact OR preview and shared-k0 risk are shown");
        check(stage.IsEnabled && !model.HasChanges, "preview alone has no pending edits");
        Capture("learn-spellblade-preview.png");
        Click(cancel);
        await Settle(window);
        check(!picker.IsVisible && learning.Preview == null && !model.HasChanges, "actual Cancel discards preview without staging");
        await Open();
        Click(stage);
        await Settle(window);
        check(!picker.IsVisible && model.Pending.Count == 2 && model.NeedsConfirmation
            && learning.ValidPending && model.Pending.All(field => field.LearningHack != null),
            "actual stage button creates two authorized byte edits and requires a second dangerous-write confirmation");
        check(!open.IsEnabled, "cannot silently rebase an already staged learning operation");
        Capture("learn-staged.png");
        model.Undo.Execute(null);
        await Settle(window);
        check(!model.HasChanges && learning.Fields.All(field => field.LearningHack == null), "Undo removes staged edits and authorization");

        // Drive the real secondary picker to prove learning waits for an equipped
        // command, rather than interpreting the pending replacement.
        var secondary = unit.SkillSlots[0];
        var change = Descendants<Button>(window).Single(button => ReferenceEquals(button.DataContext, secondary)
            && Equals(button.Content, Localizer.T("ui.skills.change")));
        Click(change);
        await Settle(window);
        var secondaryPicker = (Border)window.FindName("SkillPicker");
        Click(Descendants<Button>(secondaryPicker).Single(button => button.DataContext is SkillCandidate { Id: 6 }));
        await Settle(window);
        check(secondary.Field.IsDirty && !open.IsEnabled && learning.Status == Localizer.T("core.learn.pendingSecondary"),
            "pending secondary selection disables learning with write-and-refresh guidance");
        Click(open); // Routed event cannot bypass the handler's guard even when forced.
        await Settle(window);
        check(!picker.IsVisible, "disabled entry handler does not open a learning preview");
        model.Undo.Execute(null);
        await Settle(window);

        await Open();
        model.SelectedUnit = null;
        await Settle(window);
        check(!stage.IsEnabled && learning.Preview == null, "changing selection revokes the open preview");
        Click(stage);
        check(!model.HasChanges, "stale stage event cannot stage a former character");
        Click(cancel);
        model.SelectedUnit = unit;
        await Settle(window);
        await Open();
        model.ApplySnapshot(null);
        await Settle(window);
        check(!picker.IsEnabled && !stage.IsEnabled && learning.Preview == null, "disconnect disables the overlay and revokes preview");
        Click(cancel);
        model.ApplySnapshot(snapshot);
        await Settle(window);

        model.ApplySnapshot(WithCommand(snapshot, 74, 0x00F0));
        await Settle(window);
        await Open();
        check(learning.Preview is { LearnedCount: 4, AddedCount: 7, Mask: 0xE0FF }
            && learning.Actions.Count == 11, "Swordplay has eleven real actions and adds seven, not fifteen or sixteen");
        Capture("learn-swordplay-preview.png");
        Click(cancel);
        model.ApplySnapshot(WithCommand(snapshot, 74, 0xFEFF));
        await Settle(window);
        await Open();
        check(learning.Preview is { LearnedCount: 11, AddedCount: 0, After: 0xFEFF } && !stage.IsEnabled,
            "already full Spellblade k0 means all Swordplay actions learned; extra positions preserved");
        Click(stage);
        check(!model.HasChanges, "all-learned stage event creates no edit");
        Capture("learn-all-learned.png");
        Click(cancel);

        model.ApplySnapshot(WithCommand(snapshot, 6, 0x0082));
        await Settle(window);
        await Open();
        check(learning.Preview is { Index: 1, LearnedCount: 2, AddedCount: 12 }
            && !learning.Warning.Contains(Localizer.T("core.learn.shared")), "ordinary command uses k1, not shared k0");
        Capture("learn-ordinary-preview.png");
        Click(cancel);
        await Settle(window);
        check(!model.HasChanges, "scenario ends with no pending write and no modal was opened");
    }
}
