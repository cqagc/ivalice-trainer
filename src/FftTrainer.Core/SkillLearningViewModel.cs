namespace FftTrainer.Core;

public sealed record LearningActionPreview(int Id, int Position, string Name, bool Learned)
{
    public string Text => $"{(Learned ? Localizer.T("core.learn.already") : Localizer.T("core.learn.new"))} · {Name} · #{Id} (0x{Id:X3}) · p{Position}";
}

public sealed class SkillLearningViewModel : ObservableObject
{
    private readonly UnitViewModel unit;
    private readonly GameTextCatalog text;
    private SkillLearningPlan? live;
    private SkillIdentity? identity;
    private SkillIdentity? previewIdentity;
    private SkillLearningAuthorization? authorization;
    public SkillLearningPlan? Preview { get; private set; }
    public IReadOnlyList<EditableField> Fields { get; private set; } = [];
    private EditableField Secondary => unit.SkillSlots[0].Field;
    public bool CanPreview => live != null && live.Group == Secondary.Value
        && !Secondary.IsDirty && !Fields.Any(item => item.IsDirty)
        && authorization is not { Active: false };
    public bool CanStage => CanPreview && Preview is { AddedCount: > 0 } plan && plan.SameState(live)
        && previewIdentity == identity && plan.Actions.All(action => text.HasAbility(action.Id));
    public string Status => Localizer.T(Secondary.IsDirty || live != null && live.Group != Secondary.Value ? "core.learn.pendingSecondary"
        : Fields.Any(item => item.IsDirty) ? "core.learn.pendingLearning"
        : live == null ? "core.learn.unavailable"
        : Preview is { AddedCount: 0 } ? "core.learn.allLearned"
        : Preview != null && Preview.Actions.Any(action => !text.HasAbility(action.Id)) ? "core.learn.missingNames"
        : "core.learn.hint");
    public string Title => Preview is { } plan ? $"{text.SkillGroupName(plan.Group)} · #{plan.Group}" : "—";
    public string Counts => Preview is { } plan
        ? Localizer.F("core.learn.counts", plan.Actions.Count, plan.LearnedCount, plan.AddedCount) : "—";
    public string Region => Preview is { } plan
        ? $"k={plan.Index} · +0x{plan.Offset:X2} / +0x{plan.Offset + 1:X2} · "
            + $"{plan.Before & 255:X2} {plan.Before >> 8:X2} {plan.Passive:X2} → {plan.After & 255:X2} {plan.After >> 8:X2} {plan.Passive:X2}"
        : "—";
    public string Warning => Localizer.T("core.learn.warning")
        + (Preview?.Index == 0 ? "\n\n" + Localizer.T("core.learn.shared") : "");
    public IReadOnlyList<LearningActionPreview> Actions => Preview?.Actions
        .Select(action => new LearningActionPreview(action.Id, action.Position, text.AbilityName(action.Id), action.Learned)).ToArray() ?? [];

    public SkillLearningViewModel(UnitViewModel unit, GameTextCatalog text)
    {
        this.unit = unit;
        this.text = text;
        Secondary.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(EditableField.Value)) return;
            if (Secondary.IsDirty) Reset();
            RefreshText();
        };
    }

    public void Reset()
    {
        authorization?.Revoke();
        authorization = null;
        foreach (var field in Fields) field.SetLearningHack(null);
        Preview = null;
        previewIdentity = null;
        RefreshText();
    }

    internal void Refresh(byte[] data, SkillLearningPlan? plan)
    {
        var next = SkillIdentity.Read(data);
        if (identity != next || Preview != null && !Preview.SameState(plan)
            || authorization != null && !authorization.Plan.SameState(plan)
            || authorization is { Active: false } && !Fields.Any(item => item.IsDirty)) Reset();
        identity = next;
        live = plan;
        RefreshText();
    }

    public bool BeginPreview()
    {
        if (!CanPreview)
        {
            if (Fields.Any(item => item.IsDirty)) Reset();
            return false;
        }
        Reset();
        Preview = live;
        previewIdentity = identity;
        RefreshText();
        return true;
    }

    internal bool Stage(long generation)
    {
        if (!CanStage || identity == null || Preview == null) return false;
        var unitOffset = Secondary.Offset - FftLayout.SecondarySkill;
        authorization = new SkillLearningAuthorization(unitOffset, identity, generation, Preview);
        var stagedPlan = Preview;
        Fields = authorization.Edits().Select(edit =>
        {
            var field = new EditableField("", "Learn", edit.Offset, 1, 0, 255, edit.Before)
                { SkillIdentity = identity, LabelSource = () => $"{unit.Name} {Localizer.T("core.learn.label")}" };
            field.ChangeDescription = () => $"{field.Label} · {text.SkillGroupName(stagedPlan.Group)} #{stagedPlan.Group} · "
                + $"+0x{edit.Offset - unitOffset:X2} {field.Original:X2} → {field.Value:X2} · "
                + string.Join(", ", stagedPlan.Actions.Where(action => !action.Learned)
                    .Select(action => $"{text.AbilityName(action.Id)} #{action.Id}"))
                + " · " + Localizer.T("core.learn.warning")
                + (stagedPlan.Index == 0 ? " " + Localizer.T("core.learn.shared") : "");
            field.SetLearningHack(authorization);
            field.Value = edit.After;
            return field;
        }).ToArray();
        RefreshText();
        return Fields.Count != 0;
    }

    public bool ValidPending => !Fields.Any(item => item.IsDirty)
        || !Secondary.IsDirty && authorization is { Active: true } && authorization.Identity == identity
            && authorization.Plan.SameState(live) && Fields.Where(item => item.IsDirty).Select(item => item.ToEdit())
                .SequenceEqual(authorization.Edits());

    public void RefreshText()
    {
        Notify(nameof(CanPreview));
        Notify(nameof(CanStage));
        Notify(nameof(Status));
        Notify(nameof(Title));
        Notify(nameof(Counts));
        Notify(nameof(Region));
        Notify(nameof(Warning));
        Notify(nameof(Actions));
    }
}
