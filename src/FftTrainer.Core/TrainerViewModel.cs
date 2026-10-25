using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace FftTrainer.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action action, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
            action();
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class EditableField : ObservableObject
{
    private uint original;
    private uint value;
    private uint latest;
    private string? error;
    private string label;
    // 标签按需生成（随界面语言和队员名变化）；没有来源时用构造时给的固定文字
    public Func<string>? LabelSource { get; set; }
    public string Label
    {
        get => LabelSource?.Invoke() ?? label;
        set => label = value;
    }
    public string Key { get; }
    public ulong Offset { get; }
    public int Width { get; }
    public uint Minimum { get; private set; }
    public uint Maximum { get; private set; }
    public bool IsEditing { get; set; }
    public bool GroupDigits { get; }
    public uint DisplayMaximum { get; set; } = uint.MaxValue;
    public RelayCommand Decrease { get; }
    public RelayCommand Increase { get; }
    public RelayCommand Maximize { get; }
    public uint Original => original;
    public bool IsDirty => value != original;
    public bool IsDanger => Key == "Learn" || SecondaryHack != null || Key == "Brave" && value < 10 || Key == "Faith" && value > 94;
    public string DangerNote => Key == "Learn" ? Localizer.T("core.learn.warning")
        : SecondaryHack != null ? Localizer.T("core.skill.hackWarning") : Key == "Brave"
        ? Localizer.T("core.field.braveDanger")
        : Localizer.T("core.field.faithDanger");
    public string? Error
    {
        get => error;
        set { error = value; Notify(); Notify(nameof(HasError)); }
    }
    public bool HasError => Error != null;
    public BattleIdentity? BattleIdentity { get; set; }
    public SkillIdentity? SkillIdentity { get; set; }
    public SecondarySkillAuthorization? SecondaryHack { get; private set; }
    public SkillLearningAuthorization? LearningHack { get; private set; }
    internal void SetLearningHack(SkillLearningAuthorization? authorization)
    {
        if (LearningHack == authorization) return;
        LearningHack?.Revoke();
        LearningHack = authorization;
        Notify(nameof(LearningHack));
        Notify(nameof(IsDanger));
        Notify(nameof(DangerNote));
        RefreshDescription();
    }
    internal void SetSecondaryHack(SecondarySkillAuthorization? authorization)
    {
        if (SecondaryHack == authorization)
            return;
        SecondaryHack?.Revoke();
        SecondaryHack = authorization;
        Notify(nameof(SecondaryHack));
        Notify(nameof(IsDanger));
        Notify(nameof(DangerNote));
        RefreshDescription();
    }
    public Func<string>? ChangeDescription { get; set; }
    public void RefreshDescription() => Notify(nameof(ChangeText));
    public void RefreshText()
    {
        Notify(nameof(Label));
        Notify(nameof(DangerNote));
        Notify(nameof(ChangeText));
    }
    public string ChangeText => ChangeDescription?.Invoke() ?? $"{Label}  {original:N0} → {value:N0}";
    public uint Value
    {
        get => value;
        set
        {
            var next = Math.Clamp(value, Math.Min(Minimum, Maximum), Maximum);
            if (this.value == next)
                return;
            this.value = next;
            Error = null;
            Changed();
        }
    }
    public string Text
    {
        get => Math.Min(value, DisplayMaximum).ToString(GroupDigits ? "N0" : "0", CultureInfo.InvariantCulture);
        set
        {
            if (uint.TryParse(value.Replace(",", ""), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed)
                && parsed != Math.Min(this.value, DisplayMaximum))
                Value = parsed;
        }
    }

    public EditableField(string label, string key, ulong offset, int width, uint min, uint max,
        uint initial, bool groupDigits = false)
    {
        this.label = label;
        Key = key;
        Offset = offset;
        Width = width;
        Minimum = min;
        Maximum = max;
        original = value = latest = initial;
        GroupDigits = groupDigits;
        Decrease = new RelayCommand(() => Value = value > Minimum ? value - 1 : Minimum);
        Increase = new RelayCommand(() => Value = value < Maximum ? value + 1 : Maximum);
        Maximize = new RelayCommand(() => Value = Maximum);
    }

    private void Changed()
    {
        Notify(nameof(Value));
        Notify(nameof(Text));
        Notify(nameof(Original));
        Notify(nameof(IsDirty));
        Notify(nameof(IsDanger));
        Notify(nameof(ChangeText));
    }

    public void Refresh(uint current)
    {
        latest = current;
        if (IsDirty || IsEditing || value == current)
            return;
        original = value = current;
        Changed();
    }

    public void Revert()
    {
        SetSecondaryHack(null);
        SetLearningHack(null);
        original = value = latest;
        Error = null;
        Changed();
    }

    public void Accept()
    {
        SetSecondaryHack(null);
        SetLearningHack(null);
        original = latest = value;
        Error = null;
        Changed();
    }

    public void SetMaximum(uint maximum, uint? minimum = null)
    {
        Maximum = maximum;
        Minimum = minimum ?? Minimum;
        Notify(nameof(Maximum));
        // 只收紧用户改过的值；游戏里读来的原值超出上限也不能自动变成一条待写入的改动
        // （2026-10-17：倒地的蒂塔 HP 是 1，上限被设成 0，凭空出现「蒂塔 HP 1 → 0」）
        if (IsDirty && value > maximum)
            Value = maximum;
    }

    public MemoryEdit ToEdit() => new(Offset, Width, original, value, SkillIdentity, BattleIdentity, SecondaryHack, LearningHack);
}

public sealed class JobViewModel : ObservableObject
{
    private readonly int index;
    private int granted;
    private readonly Func<string> name;
    private SkillJob? skills;
    public string Name => name();
    public EditableField Jp { get; }
    public EditableField Total { get; }
    public EditableField LevelByte { get; }
    public string LearnedText => skills == null ? Localizer.T("core.job.learnedUnknown")
        : Localizer.F("core.job.learned", skills.Learned.Length, skills.Abilities.Length);
    public void RefreshSkills(SkillJob? skills)
    {
        this.skills = skills;
        Notify(nameof(LearnedText));
    }

    public void RefreshLanguage()
    {
        Notify(nameof(Name));
        Notify(nameof(LearnedText));
        foreach (var field in new[] { Jp, Total, LevelByte })
            field.RefreshText();
        Jp.RefreshDescription();
    }
    public bool HasGrant => granted > 0;
    public bool IsAvailable { get; private set; } = true;
    public bool IsCurrent { get; private set; }
    public bool IsDirty => Jp.IsDirty || Total.IsDirty
        || ((LevelByte.Value ^ LevelByte.Original) & (15u << ((1 - index % 2) * 4))) != 0;
    public int Level => Math.Max(JobProgress.Level((int)Total.Value),
        (int)(LevelByte.Value >> ((1 - index % 2) * 4)) & 15);
    public int Next => JobProgress.ToNextLevel((int)Total.Value);
    public IReadOnlyList<bool> Diamonds => Enumerable.Range(1, 8).Select(level => level <= Level).ToArray();
    public RelayCommand Add100 { get; }
    public RelayCommand Add500 { get; }
    public RelayCommand LevelUp { get; }
    public RelayCommand Level8 { get; }

    public JobViewModel(Func<string> name, int index, EditableField jp, EditableField total, EditableField levelByte)
    {
        this.name = name;
        this.index = index;
        Jp = jp;
        Total = total;
        LevelByte = levelByte;
        Jp.ChangeDescription = () => $"{Jp.Label[..^3]} +{granted} JP";
        Add100 = new RelayCommand(() => Grant(100), () => IsAvailable);
        Add500 = new RelayCommand(() => Grant(500), () => IsAvailable);
        LevelUp = new RelayCommand(() => Grant(Next), () => IsAvailable);
        Level8 = new RelayCommand(() => Grant(Math.Max(0, 3000 - (int)Total.Value)), () => IsAvailable);
        foreach (var field in new[] { Jp, Total, LevelByte })
            field.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EditableField.IsDirty))
                {
                    if (!IsDirty)
                        granted = 0;
                    Refresh(IsCurrent);
                }
            };
    }

    public void Grant(int amount)
    {
        if (!IsAvailable || GameData.IsJobHidden(index) || amount <= 0)
            return;
        var result = JobProgress.Grant(Jp.Value, Total.Value, (byte)LevelByte.Value, index, amount);
        if (result.Current == Jp.Value && result.Total == Total.Value && result.LevelByte == LevelByte.Value)
            return;
        granted = checked(granted + amount);
        Jp.Value = result.Current;
        Total.Value = result.Total;
        LevelByte.Value = result.LevelByte;
        Jp.RefreshDescription();
    }

    public void RefreshAvailability(byte character)
    {
        IsAvailable = GameData.IsJobAvailable(index, character);
        Notify(nameof(IsAvailable));
        Add100.Refresh();
        Add500.Refresh();
        LevelUp.Refresh();
        Level8.Refresh();
    }

    public void Refresh(bool isCurrent)
    {
        IsCurrent = isCurrent;
        Notify(nameof(IsCurrent));
        Notify(nameof(IsDirty));
        Notify(nameof(Level));
        Notify(nameof(Next));
        Notify(nameof(Diamonds));
        Jp.RefreshDescription();
    }
}

public sealed class StatViewModel : ObservableObject
{
    private readonly UnitViewModel unit;
    private readonly string nameKey;
    public int Index { get; }
    public string Name => Localizer.T(nameKey);
    public EditableField Raw { get; }
    public bool Available => unit.StatRules != null;
    public bool IsPreview => Raw.IsDirty || unit.Level.IsDirty;
    public string? Error => Raw.Error;
    public bool HasError => Raw.HasError;
    public string Text
    {
        get => Display(Raw.Value)?.ToString(CultureInfo.InvariantCulture) ?? "—";
        set
        {
            if (!Available)
                return;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target)
                || target < 1 || target > (Index < 2 ? 999 : 99))
            {
                Raw.Error = Localizer.F("core.stat.rangeError", Name, Index < 2 ? 999 : 99);
                return;
            }
            var rules = unit.StatRules!;
            if (!UnitStats.TryInverse(target, Raw.Value, rules.Multipliers[Index], rules.Bonuses[Index],
                UnitStats.Cap(Index), out var next))
            {
                Raw.Error = Localizer.F("core.stat.unreachable", Name, target);
                return;
            }
            Raw.Error = null;
            Raw.Value = next;
            Notify();
        }
    }

    public StatViewModel(UnitViewModel unit, int index, string nameKey, EditableField raw)
    {
        this.unit = unit;
        Index = index;
        this.nameKey = nameKey;
        Raw = raw;
        Raw.ChangeDescription = () => $"{unit.Name} {Name} {Display(Raw.Original)?.ToString() ?? "—"} → {Text}";
        Raw.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditableField.Value) or nameof(EditableField.Error))
                Refresh();
        };
    }

    private int? Display(uint raw) => unit.StatRules is { } rules
        ? UnitStats.Display(raw, rules.Multipliers[Index], rules.Bonuses[Index], UnitStats.Cap(Index)) : null;

    public void Refresh()
    {
        if (!Raw.IsEditing || !Available)
            Notify(nameof(Text));
        Notify(nameof(Available));
        Notify(nameof(IsPreview));
        Notify(nameof(Error));
        Notify(nameof(HasError));
        Raw.RefreshDescription();
    }

    public void RefreshLanguage()
    {
        Notify(nameof(Name));
        Raw.RefreshText();
    }
}

public sealed record EquipmentViewModel(string Name, string ItemName, string Id, string Category = "");

public sealed record SkillCandidate(int Id, string Name)
{
    public string IdText => $"#{Id} · 0x{Id:X3}";
}

public sealed class SkillSlotViewModel : ObservableObject
{
    private readonly GameTextCatalog text;
    private SkillIdentity? identity;
    private uint current;
    private bool supported;
    private readonly string nameKey;
    private UnitSkillRules? rules;
    private bool secondaryHackEnabled;
    internal long Generation { get; set; }
    public bool IsSecondary => Slot == SkillSlot.Secondary;
    public bool SecondaryHackEnabled
    {
        get => secondaryHackEnabled;
        set
        {
            value = value && IsSecondary && CanChange;
            if (secondaryHackEnabled == value)
                return;
            secondaryHackEnabled = value;
            Field.SetSecondaryHack(null);
            BuildCandidates();
            Notify();
        }
    }
    public void ResetSecondaryHack()
    {
        SecondaryHackEnabled = false;
        Field.SetSecondaryHack(null);
    }
    public SkillSlot Slot { get; }
    public string Name => Localizer.T(nameKey);
    public EditableField Field { get; }
    public IReadOnlyList<SkillCandidate> Candidates { get; private set; } = [];
    public bool CanChange => supported && identity == Field.SkillIdentity
        && UnitSkillRules.Matches(Slot, (int)current);
    public string DisplayName => Describe(Field.Value);
    public string IdText => $"#{Field.Value} · 0x{Field.Value:X3}";
    private string Describe(uint id) => Slot == SkillSlot.Secondary
        ? text.SkillGroupName((int)id) : text.AbilityName((int)id);

    public SkillSlotViewModel(SkillSlot slot, string nameKey, EditableField field, GameTextCatalog text)
    {
        Slot = slot;
        this.nameKey = nameKey;
        Field = field;
        this.text = text;
        Field.ChangeDescription = () => $"{Field.Label} {Describe(Field.Original)} → {Describe(Field.Value)}"
            + (Field.SecondaryHack != null ? " · " + Localizer.T("core.skill.hackWarning") : "");
        Field.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditableField.Value))
            {
                Notify(nameof(DisplayName));
                Notify(nameof(IdText));
            }
        };
    }

    public void Refresh(UnitSkillRules? rules, byte[] data)
    {
        var nextIdentity = SkillIdentity.Read(data);
        if (identity != nextIdentity || rules is not { Supported: true })
            ResetSecondaryHack();
        identity = nextIdentity;
        if (!Field.IsDirty)
            Field.SkillIdentity = identity;
        current = (uint)Values.Bits(data.AsSpan(UnitSkillRules.Offset(Slot)),
            Slot == SkillSlot.Secondary ? ValueTypeCode.U8 : ValueTypeCode.U16);
        supported = rules is { Supported: true };
        this.rules = rules;
        BuildCandidates();
        Notify(nameof(CanChange));
        Notify(nameof(DisplayName));
        Notify(nameof(IdText));
    }

    private void BuildCandidates()
    {
        Candidates = supported ? (SecondaryHackEnabled ? rules!.HackSecondary : rules!.Candidates(Slot))
            .Where(id => id == 0 || Slot == SkillSlot.Secondary || text.HasAbility(id))
            .Select(id => new SkillCandidate(id, Describe((uint)id))).ToArray() : [];
        Notify(nameof(Candidates));
    }

    public void RefreshLanguage()
    {
        BuildCandidates();
        Notify(nameof(Name));
        Notify(nameof(DisplayName));
        Field.RefreshText();
    }

    public bool Select(int id)
    {
        if (!CanChange || Candidates.All(candidate => candidate.Id != id))
            return false;
        Field.SetSecondaryHack(SecondaryHackEnabled && identity != null
            ? new SecondarySkillAuthorization(Field.Offset, identity, (uint)id, Generation) : null);
        Field.Value = (uint)id;
        return true;
    }

    public bool ValidPending => !Field.IsDirty || CanChange
        && (Field.SecondaryHack is { } hack
            ? SecondaryHackEnabled && hack.Allows(Field.ToEdit()) && rules!.HackSecondary.Contains((int)Field.Value)
            : rules!.Candidates(Slot).Contains((int)Field.Value))
        && Candidates.Any(candidate => candidate.Id == Field.Value);
}

public sealed class UnitViewModel : ObservableObject
{
    private readonly GameTextCatalog text;
    private UnitSnapshot? last;
    private FftLayout? lastLayout;
    public string PrimarySkill { get; private set; } = "—";
    public IReadOnlyList<SkillSlotViewModel> SkillSlots { get; }
    public SkillLearningViewModel Learning { get; }
    public void ResetSkillHacks()
    {
        foreach (var slot in SkillSlots) slot.ResetSecondaryHack();
        Learning.Reset();
    }
    public UnitStatRules? StatRules { get; private set; }
    public IReadOnlyList<StatViewModel> Stats { get; }
    public IEnumerable<StatViewModel> DisplayStats => new[] { Stats[0], Stats[1], Stats[3], Stats[4], Stats[2] };
    public string Move => StatRules?.Move.ToString() ?? "—";
    public string Jump => StatRules?.Jump.ToString() ?? "—";
    public RelayCommand Gain1 { get; }
    public RelayCommand Gain5 { get; }
    public RelayCommand Gain99 { get; }

    public int Slot { get; }
    public string Name { get; private set; } = "";
    public bool IsProtagonist { get; private set; }
    public string Gender { get; private set; } = "";
    public string Job { get; private set; } = "";
    public PortraitIdentity Portrait { get; private set; } = new(0, 0, "unknown");
    public string Initial => Name[..1];
    public string Number => $"No.{Slot + 1:00}" + (IsProtagonist ? Localizer.T("core.unit.protagonistSuffix") : "");
    public EditableField Level { get; }
    public EditableField Experience { get; }
    public EditableField Brave { get; }
    public EditableField Faith { get; }
    public IReadOnlyList<JobViewModel> Jobs { get; }
    public IReadOnlyList<EquipmentViewModel> Equipment { get; private set; } = [];
    public IEnumerable<EditableField> Fields => new[] { Level, Experience, Brave, Faith }
        .Concat(SkillSlots.Select(slot => slot.Field))
        .Concat(Learning.Fields)
        .Concat(Stats.Select(stat => stat.Raw))
        .Concat(Jobs.SelectMany(job => new[] { job.Jp, job.Total, job.LevelByte }).Distinct());
    public bool IsDirty => Fields.Any(item => item.IsDirty);
    internal void RefreshDirty() => Notify(nameof(IsDirty));
    public RelayCommand AddAll500 { get; }
    public RelayCommand AllLevel8 { get; }

    public UnitViewModel(UnitSnapshot snapshot, FftLayout layout, GameTextCatalog? text = null)
    {
        this.text = text ?? new GameTextCatalog();
        StatRules = snapshot.Stats;
        Slot = snapshot.Slot;
        RefreshIdentity(snapshot.Data);
        var start = FftLayout.Roster + (ulong)(Slot * layout.RosterStride);
        // 标签 = 队员名 + 字段名，两者都按需取（切换语言、队员改名后自动更新）
        EditableField Field(Func<string> label, string key, int offset, int width, uint min, uint max) =>
            new("", key, start + (ulong)offset, width, min, max, Read(snapshot.Data, offset, width))
                { LabelSource = () => $"{Name} {label()}" };
        Func<string> Loc(string key) => () => Localizer.T(key);
        Level = Field(Loc("core.unit.level"), "Level", FftLayout.Level, 1, 1, 99);
        Experience = Field(Loc("core.unit.experience"), "Experience", FftLayout.Experience, 1, 0, 99);
        Brave = Field(Loc("core.unit.brave"), "Brave", FftLayout.Brave, 1, 0, 100);
        Faith = Field(Loc("core.unit.faith"), "Faith", FftLayout.Faith, 1, 0, 100);
        string[] slotNames = ["core.skill.secondary", "core.skill.reaction", "core.skill.support", "core.skill.movement"];
        SkillSlots = Enum.GetValues<SkillSlot>().Select(slot => new SkillSlotViewModel(slot, slotNames[(int)slot],
            Field(Loc(slotNames[(int)slot]), "Skill", UnitSkillRules.Offset(slot), slot == SkillSlot.Secondary ? 1 : 2,
                0, slot == SkillSlot.Secondary ? 255u : 65535u), this.text)).ToArray();
        Learning = new SkillLearningViewModel(this, this.text);
        string[] statNames = ["core.stat.maxHp", "core.stat.maxMp", "core.stat.speed", "core.stat.pa", "core.stat.ma"];
        Stats = statNames.Select((name, i) => new StatViewModel(this, i, name,
            Field(Loc(name), "RawStat", FftLayout.RawStats + i * 3, 3, 0, UnitStats.RawMaximum))).ToArray();
        Gain1 = new RelayCommand(() => Grow(1), () => StatRules != null && Level.Value < 99);
        Gain5 = new RelayCommand(() => Grow(5), () => StatRules != null && Level.Value < 99);
        Gain99 = new RelayCommand(() => Grow(99), () => StatRules != null && Level.Value < 99);
        Level.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditableField.Value))
                RefreshStats();
        };
        var levels = Enumerable.Range(0, FftLayout.JobCount / 2)
            .Select(i => Field(Loc("core.unit.jobLevels"), "JobLevels", FftLayout.JobLevels + i, 1, 0, 255)).ToArray();
        Jobs = Enumerable.Range(0, FftLayout.JobCount)
            .Where(i => !GameData.IsJobHidden(i))
            .Select(i =>
            {
                string JobName() => this.text.JobName(0x4A + i);
                return new JobViewModel(JobName, i,
                    Field(() => JobName() + " JP", "Jp", FftLayout.CurrentJp + i * 2, 2, 0, 9999),
                    Field(() => JobName() + " J-EXP", "TotalJp", FftLayout.TotalJp + i * 2, 2, 0, 9999),
                    levels[i / 2]);
            }).ToArray();
        foreach (var field in Fields)
            field.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EditableField.IsDirty))
                    Notify(nameof(IsDirty));
            };
        AddAll500 = new RelayCommand(() =>
        {
            foreach (var job in Jobs)
                job.Add500.Execute(null);
        });
        AllLevel8 = new RelayCommand(() =>
        {
            foreach (var job in Jobs)
                job.Level8.Execute(null);
        });
        Refresh(snapshot, layout);
    }

    public void Grow(int levels)
    {
        if (StatRules == null || Level.Value >= 99)
            return;
        var target = Math.Min(99, (int)Level.Value + levels);
        var raw = UnitStats.Grow(Stats.Select(stat => stat.Raw.Value).ToArray(), (int)Level.Value, target, StatRules.Growth);
        for (var i = 0; i < Stats.Count; i++)
            Stats[i].Raw.Value = raw[i];
        Level.Value = (uint)target;
    }

    private void RefreshStats()
    {
        foreach (var stat in Stats)
            stat.Refresh();
        Notify(nameof(Move));
        Notify(nameof(Jump));
        Gain1.Refresh();
        Gain5.Refresh();
        Gain99.Refresh();
    }

    private static uint Read(byte[] data, int offset, int width) => (uint)Values.Bits(data.AsSpan(offset),
        width == 1 ? ValueTypeCode.U8 : width == 3 ? ValueTypeCode.U24 : ValueTypeCode.U16);

    private void RefreshIdentity(byte[] data)
    {
        Name = text.UnitName((int)Read(data, FftLayout.NameId, 2));
        IsProtagonist = data[FftLayout.Character] == 0x01;
        var female = data[FftLayout.Character] == 0x81;
        Gender = Localizer.T(female ? "core.gender.female" : "core.gender.male");
        var portrait = new PortraitIdentity(data[FftLayout.Character], data[FftLayout.Job],
            data[FftLayout.Character] == 0x82 ? "monster" : female ? "female" : "male");
        if (Portrait != portrait)
        {
            Portrait = portrait;
            Notify(nameof(Portrait));
        }
        Job = text.JobName(data[FftLayout.Job]);
        Notify(nameof(Name));
        Notify(nameof(Initial));
        Notify(nameof(Number));
        Notify(nameof(IsProtagonist));
        Notify(nameof(Gender));
        Notify(nameof(Job));
    }

    // 切换界面语言后：用最近一次快照重算所有文字。字段只在未改动时刷新数值，待写入的改动不受影响。
    public void RefreshLanguage()
    {
        if (last != null && lastLayout != null)
            Refresh(last, lastLayout);
        foreach (var job in Jobs)
            job.RefreshLanguage();
        foreach (var stat in Stats)
            stat.RefreshLanguage();
        foreach (var slot in SkillSlots)
            slot.RefreshLanguage();
        foreach (var field in new[] { Level, Experience, Brave, Faith })
            field.RefreshText();
        Notify(nameof(Number));
    }

    public void Refresh(UnitSnapshot snapshot, FftLayout layout)
    {
        last = snapshot;
        lastLayout = layout;
        var previousName = Name;
        StatRules = snapshot.Stats;
        RefreshIdentity(snapshot.Data);
        if (previousName != Name)
            foreach (var field in Fields)
                field.RefreshText();
        var start = FftLayout.Roster + (ulong)(Slot * layout.RosterStride);
        var staged = Jobs.Where(job => job.IsDirty)
            .SelectMany(job => new[] { job.Jp, job.Total, job.LevelByte }).ToHashSet();
        if (Level.IsDirty && StatRules != null)
            staged.UnionWith(Stats.Select(stat => stat.Raw));
        foreach (var field in Fields)
        {
            var wasEditing = field.IsEditing;
            field.IsEditing |= staged.Contains(field);
            field.Refresh(Read(snapshot.Data, (int)(field.Offset - start), field.Width));
            field.IsEditing = wasEditing;
        }
        for (var i = 0; i < Jobs.Count; i++)
        {
            Jobs[i].RefreshSkills(snapshot.Skills?.Jobs.FirstOrDefault(job => job.Index == i));
            Jobs[i].RefreshAvailability(snapshot.Data[FftLayout.Character]);
            Jobs[i].Refresh(i == GameData.JobIndex(snapshot.Data[FftLayout.Job]));
        }
        PrimarySkill = snapshot.Skills is { } skills
            ? $"{text.SkillGroupName(skills.Primary)}  · #{skills.Primary}" : Localizer.T("core.unit.skillTableFailed");
        Notify(nameof(PrimarySkill));
        foreach (var slot in SkillSlots)
            slot.Refresh(snapshot.Skills, snapshot.Data);
        Learning.Refresh(snapshot.Data, snapshot.Skills?.Learning);
        string[] names = ["head", "body", "accessory", "rightWeapon", "rightShield", "leftWeapon", "leftShield"];
        Equipment = names.Select((name, i) =>
        {
            var id = Read(snapshot.Data, FftLayout.Equipment + i * 2, 2);
            return new EquipmentViewModel(Localizer.T("core.equip." + name),
                id == 0xFF ? Localizer.T("core.equip.empty") : text.ItemName((int)id), $"0x{id:X2}",
                id == 0xFF ? "" : text.ItemCategory((int)id));
        }).ToArray();
        Notify(nameof(Equipment));
        RefreshStats();
    }
}

public sealed class BattleUnitViewModel : ObservableObject
{
    private readonly IReadOnlyList<UnitSnapshot> roster;
    private readonly GameTextCatalog text;
    public BattleUnit Unit { get; private set; }
    public string Name { get; private set; } = "";
    public string Job { get; private set; } = "";
    public string Level => Localizer.F("core.unit.levelShort", Unit.Data[0x29]);
    public string State => Unit.IsDown
        ? Unit.DeathCountdown == 0xFF ? Localizer.T("core.battle.downNoCountdown")
            : Localizer.F("core.battle.downCountdown", Unit.DeathCountdown)
        : "";
    public bool CanChangeHp => !Unit.IsDown && Unit.Hp > 0;
    public string Experience => Localizer.F("core.unit.expShort", Unit.Data[0x28]);
    public string FactionLabel => BattleRules.IsTarget(Unit) ? "TARGET"
        : Unit.Side == BattleSide.Enemy ? "ENEMY" : Unit.Side == BattleSide.Guest ? "GUEST" : "";
    public string JobLevel => Unit.JobLevel is >= 1 and <= 8 ? Unit.JobLevel.Value.ToString() : "—";
    public string JobNext => Unit.JobLevel == 8 ? "MAX"
        : Unit.JobLevel is >= 1 and < 8 && Unit.TotalJp is { } total
            ? JobProgress.ToNextLevel((int)total).ToString() : "—";
    public bool HasKnownJob => Unit.JobIndex >= 0;
    public string JobProgressText => HasKnownJob ? Localizer.F("core.battle.jobProgress", JobLevel, JobNext, JobJp) : Localizer.T("core.battle.uniqueJob");
    public string JobJp => Unit.CurrentJp?.ToString() ?? "—";
    public PortraitIdentity? Portrait { get; }
    public EditableField Ct { get; }
    // 游戏界面把 CT 封顶显示为 100；内存可以超过 100（已攒满、排队待行动）
    public string CtLabel => Unit.Ct >= 100 ? Localizer.T("core.battle.ready") : "";
    public string CtTip => Localizer.F("core.battle.ctTip", Unit.Ct);
    public uint MaxHp => Unit.MaxHp;
    public uint MaxMp => Unit.MaxMp;
    public EditableField Hp { get; }
    public EditableField Mp { get; }
    public EditableField Brave { get; }
    public EditableField Faith { get; }
    public IEnumerable<EditableField> Fields => new[] { Hp, Mp, Brave, Faith, Ct };

    public BattleUnitViewModel(BattleUnit unit, IReadOnlyList<UnitSnapshot> roster, GameTextCatalog text)
    {
        Unit = unit;
        this.roster = roster;
        this.text = text;
        RefreshNames();
        Portrait = new PortraitIdentity(unit.Data[0], unit.Data[3], (unit.Data[6] & 0x20) != 0
            ? "monster" : unit.Data[0] == 0x81 ? "female" : "male");
        EditableField Field(string labelKey, int offset, int width, uint max, uint min = 0) =>
            new("", "Battle", unit.Offset + (ulong)offset, width, min, max, unit.Read(offset, width))
            {
                BattleIdentity = unit.Identity,
                LabelSource = () => Name.Replace(" · ", " ") + " " + Localizer.T(labelKey),
            };
        // 活着的单位 HP 下限 1：写成 0 会出现「HP 为 0 但没有倒下状态位」的矛盾状态
        // 倒下的单位 HP 锁在当前值（剧情人质蒂塔倒地时 HP 是 1，不能改成 0）
        var alive = !(unit.IsDown || unit.Hp == 0);
        Hp = Field("core.battle.field.hp", 0x30, 2, alive ? unit.MaxHp : unit.Hp, alive ? 1u : unit.Hp);
        Mp = Field("core.battle.field.mp", 0x34, 2, unit.MaxMp);
        Brave = Field("core.battle.field.brave", 0x2A, 1, 100);
        Faith = Field("core.battle.field.faith", 0x2C, 1, 100);
        Ct = Field("core.battle.field.ct", 0x41, 1, 100);
        Ct.DisplayMaximum = 100;
    }

    private void RefreshNames()
    {
        var unit = Unit;
        Job = text.JobName(unit.Data[3]);
        var matches = roster.Where(item => item.Data[0] == unit.Data[0]
            && item.Data[FftLayout.Level] == unit.Data[0x29]
            && item.Data[FftLayout.Brave] == unit.Data[0x2B]
            && item.Data[FftLayout.Faith] == unit.Data[0x2D]).ToArray();
        var match = unit.Side == BattleSide.Player && matches.Length == 1 ? matches[0] : null;
        var side = Localizer.T(unit.Side == BattleSide.Player ? "core.side.player"
            : unit.Side == BattleSide.Guest ? "core.side.guest" : "core.side.enemy");
        Name = text.KnownUnitName(unit.NameId)
            ?? (match != null ? text.UnitName((int)Values.Bits(match.Data.AsSpan(FftLayout.NameId), ValueTypeCode.U16))
                : unit.Side == BattleSide.Guest ? Job : side + " · " + Job);
    }

    // 只换文字：名字和职业按当前单位数据重算（单位身份变了卡片会整张重建）
    public void RefreshLanguage()
    {
        RefreshNames();
        Notify(string.Empty);
        foreach (var field in Fields)
            field.RefreshText();
    }

    public void Refresh(BattleUnit unit)
    {
        Unit = unit;
        Notify(nameof(FactionLabel));
        Notify(nameof(JobLevel));
        Notify(nameof(JobNext));
        Notify(nameof(JobJp));
        Notify(nameof(JobProgressText));
        Notify(nameof(Experience));
        Notify(nameof(CtLabel));
        Notify(nameof(CtTip));
        var alive = !(unit.IsDown || unit.Hp == 0);
        Hp.SetMaximum(alive ? unit.MaxHp : unit.Hp, alive ? 1u : unit.Hp);
        Mp.SetMaximum(unit.MaxMp);
        foreach (var field in Fields)
            field.Refresh(unit.Read((int)(field.Offset - unit.Offset), field.Width));
        Notify(nameof(Ct));
        Notify(nameof(MaxHp));
        Notify(nameof(MaxMp));
        Notify(nameof(State));
        Notify(nameof(CanChangeHp));
    }
}

public sealed class InventoryItemViewModel : ObservableObject
{
    private readonly GameTextCatalog text;
    public int Id { get; }
    public string HexId => $"0x{Id:X2}";
    public bool IsPoaching { get; }
    public string Name => IsPoaching ? text.PoachingName(Id) : text.ItemName(Id);
    public string Category => IsPoaching ? Localizer.T("core.item.group.poaching") : text.ItemCategory(Id);
    public string Description => IsPoaching ? "" : text.ItemDescription(Id);
    private bool IsEquipment => !IsPoaching && GroupKey is "equipment" or "bonus";
    private EquipmentStats? Stats => IsEquipment ? text.ItemStats(Id) : null;
    public string StatSummary => !IsEquipment ? "" : Stats?.Summary ?? Localizer.T("core.item.stats.unknown");
    public bool HasDetails => Description.Length > 0 || IsEquipment;
    public string DetailsText
    {
        get
        {
            if (IsPoaching)
                return Name;
            var parts = new List<string> { Name };
            if (Description.Length > 0)
                parts.Add(Description);
            if (IsEquipment)
            {
                var summary = StatSummary;
                if (summary.Length > 0)
                    parts.Add(summary);
                if (Stats != null)
                    parts.Add(Localizer.T("core.item.stats.source"));
                parts.Add(Localizer.T("core.item.stats.limitations"));
            }
            return string.Join("\n\n", parts);
        }
    }
    // 筛选用和语言无关的代码（consumable / equipment / bonus / poaching），显示用 Group
    public string GroupKey => IsPoaching ? "poaching" : text.ItemGroupKey(Id);
    public string Group => Localizer.T("core.item.group." + GroupKey);
    public EditableField Quantity { get; }

    public InventoryItemViewModel(int id, byte quantity, GameTextCatalog? text = null, bool isPoaching = false)
    {
        this.text = text ?? new GameTextCatalog();
        if (id < 0 || id >= (isPoaching ? FftLayout.PoachingCount : FftLayout.InventoryCount))
            throw new ArgumentOutOfRangeException(nameof(id));
        IsPoaching = isPoaching;
        Id = id;
        Quantity = new EditableField("", isPoaching ? "Poaching" : "Inventory",
            (isPoaching ? FftLayout.Poaching : FftLayout.Inventory) + (ulong)id,
            1, 0, 99, quantity) { LabelSource = () => Localizer.F("core.item.quantityLabel", Name) };
    }

    public void RefreshLanguage()
    {
        Notify(string.Empty);
        Quantity.RefreshText();
    }
}

public sealed class TrainerViewModel : ObservableObject
{
    private readonly GameTextCatalog text;
    private readonly IGameSession session;
    private readonly SaveBackups? backups;
    private readonly FftLayout layout;
    private long generation;
    private readonly Dictionary<(int Id, bool Poaching), InventoryItemViewModel> itemsById = [];
    private int refreshDepth;
    private bool itemsRefreshRequested;
    private bool pendingRefreshRequested;
    private bool lastHasChanges;
    private bool lastHasJpChanges;
    private bool lastNeedsConfirmation;
    private long optionsVersion;
    private readonly GameTextService? gameText;
    private UnitViewModel? selectedUnit;
    private bool connected;
    private bool busy;
    private int tab;
    private bool showAllItems;
    private bool inBattle;
    private bool lockHp;
    private bool infiniteMp;
    private bool guestLockHp;
    private bool freezeCountdown;
    private bool lockCt;
    private bool alwaysMove;
    private bool alwaysAct;
    private bool maxMoveJump;
    private bool instantCast;
    private int experienceMultiplier = 1;
    private int jpMultiplier = 1;
    private bool keepBattleOptions;
    private readonly string? preferencesPath;
    private BattlePreferences? savedPreferences;
    public int ExperienceMultiplier
    {
        get => experienceMultiplier;
        set { experienceMultiplier = CanSetBattleOptions && value is 1 or 2 or 5 or 10 ? value : 1; SetBattleOptions(); }
    }
    public int JpMultiplier
    {
        get => jpMultiplier;
        set { jpMultiplier = CanSetBattleOptions && value is 1 or 2 or 5 or 10 ? value : 1; SetBattleOptions(); }
    }
    // 保留战斗设置：战斗结束不自动关闭，下一场（包括重启修改器后）自动生效；不在战斗时也能预先打开
    public bool KeepBattleOptions
    {
        get => keepBattleOptions;
        set
        {
            keepBattleOptions = value;
            if (!value && !InBattle)
            {
                lockHp = infiniteMp = guestLockHp = freezeCountdown = lockCt = alwaysMove = alwaysAct = maxMoveJump = instantCast = false;
                experienceMultiplier = jpMultiplier = 1;
            }
            SetBattleOptions();
        }
    }
    public bool CanSetBattleOptions => CanEdit && (inBattle || keepBattleOptions);
    public string BackupText => backups?.LastBackup is { } time
        ? Localizer.F("core.backup.at", time) : Localizer.T("core.backup.none");
    public string? BackupDirectory => backups?.BackupDirectory;
    public void ReportBackupError(string error)
    {
        Status = Localizer.F("core.backup.openFailed", error);
        Notify(nameof(Status));
    }
    public ObservableCollection<BattleUnitViewModel> Allies { get; } = [];
    public ObservableCollection<BattleUnitViewModel> Guests { get; } = [];
    public ObservableCollection<BattleUnitViewModel> Enemies { get; } = [];
    public bool InBattle => inBattle && CanEdit;
    public string BattleStatus => inBattle ? Localizer.F("core.battle.status", Allies.Count, Guests.Count, Enemies.Count)
        : Localizer.T("core.battle.notInBattle");
    public bool LockHp
    {
        get => lockHp;
        set { lockHp = value && CanSetBattleOptions; SetBattleOptions(); }
    }
    public bool GuestLockHp
    {
        get => guestLockHp;
        set { guestLockHp = value && CanSetBattleOptions; SetBattleOptions(); }
    }
    public bool InfiniteMp
    {
        get => infiniteMp;
        set { infiniteMp = value && CanSetBattleOptions; SetBattleOptions(); }
    }
    public bool FreezeCountdown
    {
        get => freezeCountdown;
        set { freezeCountdown = value && CanSetBattleOptions; SetBattleOptions(); }
    }
    public bool LockCt
    {
        get => lockCt;
        set { lockCt = value && CanSetBattleOptions; SetBattleOptions(); }
    }
    public bool AlwaysMove
    {
        get => alwaysMove;
        set { alwaysMove = value && CanSetBattleOptions; SetBattleOptions(); }
    }
    public bool AlwaysAct
    {
        get => alwaysAct;
        set { alwaysAct = value && CanSetBattleOptions; SetBattleOptions(); }
    }
    public bool MaxMoveJump
    {
        get => maxMoveJump;
        set { maxMoveJump = value && CanSetBattleOptions; SetBattleOptions(); }
    }
    public bool InstantCast
    {
        get => instantCast;
        set { instantCast = value && CanSetBattleOptions; SetBattleOptions(); }
    }
    public RelayCommand FillPlayerCt { get; }
    public RelayCommand WeakenEnemies { get; }
    public RelayCommand DisableBattleOptions { get; }

    private void SetBattleOptions()
    {
        optionsVersion++;
        (session as IBattleSession)?.SetBattleOptions(lockHp, infiniteMp, guestLockHp, freezeCountdown, lockCt, experienceMultiplier, jpMultiplier, alwaysMove, alwaysAct, maxMoveJump, instantCast, optionsVersion, keepBattleOptions);
        SavePreferences();
        Notify(nameof(KeepBattleOptions));
        Notify(nameof(CanSetBattleOptions));
        Notify(nameof(LockHp));
        Notify(nameof(GuestLockHp));
        Notify(nameof(InfiniteMp));
        Notify(nameof(FreezeCountdown));
        Notify(nameof(LockCt));
        Notify(nameof(AlwaysMove));
        Notify(nameof(AlwaysAct));
        Notify(nameof(MaxMoveJump));
        Notify(nameof(InstantCast));
        Notify(nameof(ExperienceMultiplier));
        Notify(nameof(JpMultiplier));
    }

    private void SavePreferences()
    {
        if (preferencesPath == null)
            return;
        var current = keepBattleOptions
            ? new BattlePreferences(true, lockHp, infiniteMp, guestLockHp, freezeCountdown, lockCt, alwaysMove, alwaysAct,
                maxMoveJump, instantCast, experienceMultiplier, jpMultiplier)
            : new BattlePreferences();
        if (current == savedPreferences)
            return;
        current.Save(preferencesPath);
        savedPreferences = current;
    }

    private void RefreshBattle(GameSnapshot? snapshot)
    {
        var battle = snapshot?.Battle ?? [];
        var hadEffects = lockHp || infiniteMp || guestLockHp || freezeCountdown || lockCt || alwaysMove || alwaysAct || maxMoveJump || instantCast
            || experienceMultiplier != 1 || jpMultiplier != 1;
        inBattle = BattleRules.IsInBattle(battle);
        // 只接收当前设置的回读；断开连接时清空所有效果（保留战斗设置时原样保留，会话那边也不清）。
        var keep = keepBattleOptions;
        if (snapshot == null ? !keep : snapshot.OptionsVersion == optionsVersion)
        {
            lockHp = snapshot?.LockHp ?? false;
            infiniteMp = snapshot?.InfiniteMp ?? false;
            guestLockHp = snapshot?.GuestLockHp ?? false;
            freezeCountdown = snapshot?.FreezeCountdown ?? false;
            lockCt = snapshot?.LockCt ?? false;
            alwaysMove = (inBattle || keep) && (snapshot?.AlwaysMove ?? false);
            alwaysAct = (inBattle || keep) && (snapshot?.AlwaysAct ?? false);
            maxMoveJump = (inBattle || keep) && (snapshot?.MaxMoveJump ?? false);
            instantCast = (inBattle || keep) && (snapshot?.InstantCast ?? false);
            experienceMultiplier = inBattle || keep ? snapshot?.ExperienceMultiplier ?? 1 : 1;
            jpMultiplier = inBattle || keep ? snapshot?.JpMultiplier ?? 1 : 1;
            // 会话自己关掉的（写入失败）也要记下来，免得重启后又自动打开
            if (keep)
                SavePreferences();
        }
        var notice = snapshot?.BattleNotice
            ?? (hadEffects && !inBattle && !keep ? Localizer.T("core.battle.autoOffOutside") : null);
        if (notice != null)
        {
            Status = notice;
            Notify(nameof(Status));
        }
        foreach (var collection in new[] { Allies, Guests, Enemies })
        {
            var side = collection == Allies ? BattleSide.Player
                : collection == Guests ? BattleSide.Guest : BattleSide.Enemy;
            var current = inBattle ? battle.Where(unit => unit.Side == side).ToArray() : [];
            foreach (var old in collection.Where(old => generation != snapshot?.Generation
                || !current.Any(unit => unit.Slot == old.Unit.Slot && unit.Identity == old.Unit.Identity)).ToArray())
                collection.Remove(old);
            foreach (var unit in current)
            {
                var model = collection.FirstOrDefault(item => item.Unit.Slot == unit.Slot);
                if (model == null)
                {
                    model = new BattleUnitViewModel(unit, snapshot!.Units, text);
                    foreach (var field in model.Fields)
                        Watch(field);
                    collection.Add(model);
                }
                else
                    model.Refresh(unit);
            }
        }
        Notify(nameof(InBattle));
        Notify(nameof(CanSetBattleOptions));
        Notify(nameof(BattleStatus));
        Notify(nameof(LockHp));
        Notify(nameof(GuestLockHp));
        Notify(nameof(InfiniteMp));
        Notify(nameof(FreezeCountdown));
        Notify(nameof(LockCt));
        Notify(nameof(AlwaysMove));
        Notify(nameof(AlwaysAct));
        Notify(nameof(MaxMoveJump));
        Notify(nameof(InstantCast));
        Notify(nameof(ExperienceMultiplier));
        Notify(nameof(JpMultiplier));
        WeakenEnemies.Refresh();
        FillPlayerCt.Refresh();
        RefreshPending();
    }
    private string itemSearch = "";
    private string itemCategory = "all";
    public ObservableCollection<InventoryItemViewModel> Items { get; } = [];
    public ObservableCollection<InventoryItemViewModel> VisibleItems { get; } = [];
    public bool ShowAllItems
    {
        get => showAllItems;
        set { showAllItems = value; Notify(); RefreshItems(); }
    }
    public string ItemSearch
    {
        get => itemSearch;
        set { itemSearch = value; Notify(); RefreshItems(); }
    }
    public string ItemCategory
    {
        get => itemCategory;
        set { itemCategory = value; Notify(); RefreshItems(); }
    }
    public RelayCommand ShowAllCategories { get; }
    public RelayCommand ShowConsumables { get; }
    public RelayCommand ShowEquipment { get; }
    public RelayCommand ShowBonusItems { get; }
    public RelayCommand ShowPoaching { get; }
    public RelayCommand MaxVisibleItems { get; }
    public string RosterCountText => $"{Units.Count} / 50";
    public ObservableCollection<UnitViewModel> Units { get; } = [];
    public ObservableCollection<EditableField> Pending { get; } = [];
    public EditableField Gil { get; } = new("", "Gil", FftLayout.Gil, 4, 0, 99999999, 0, true)
        { LabelSource = () => Localizer.T("core.gil") };
    public IEnumerable<EditableField> Fields => new[] { Gil }.Concat(Units.SelectMany(unit => unit.Fields))
        .Concat(Items.Select(item => item.Quantity))
        .Concat(Allies.Concat(Guests).Concat(Enemies).SelectMany(unit => unit.Fields));
    public bool IsConnected => connected;
    public bool CanEdit => connected && !busy;
    public string ConnectionText => Localizer.T(connected ? "core.connection.connected" : "core.connection.waiting");
    public string Status { get; private set; } = Localizer.T("core.status.staged");
    public bool HasChanges => Pending.Count > 0;
    public bool HasJpChanges => Units.SelectMany(unit => unit.Jobs).Any(job => job.IsDirty);
    public bool NeedsConfirmation => Pending.Any(item => item.IsDanger);
    public RelayCommand Undo { get; }
    public UnitViewModel? SelectedUnit
    {
        get => selectedUnit;
        set
        {
            if (selectedUnit != value)
                foreach (var unit in Units)
                    unit.ResetSkillHacks();
            selectedUnit = value;
            Notify();
        }
    }
    public int Tab
    {
        get => tab;
        set { tab = value; Notify(); }
    }

    public TrainerViewModel(IGameSession session, FftLayout? layout = null, GameTextCatalog? text = null, SaveBackups? backups = null,
        string? preferencesPath = null, GameTextService? gameText = null)
    {
        this.preferencesPath = preferencesPath;
        this.gameText = gameText;
        this.text = text ?? new GameTextCatalog();
        this.backups = backups;
        this.session = session;
        this.layout = layout ?? new FftLayout();
        WeakenEnemies = new RelayCommand(() =>
        {
            var edits = BattleRules.Weaken(Allies.Concat(Guests).Concat(Enemies).Select(unit => unit.Unit).ToArray());
            foreach (var edit in edits)
                Enemies.Single(unit => unit.Hp.Offset == edit.Offset).Hp.Value = edit.After;
        }, () => InBattle);
        FillPlayerCt = new RelayCommand(() =>
        {
            // 只提升：内存 CT 已经 ≥100（排队待行动）的不动，否则会把 104 改成 100 反而降低
            foreach (var unit in Allies.Where(unit => !unit.Unit.IsDown && unit.Unit.Hp > 0 && unit.Unit.Ct < 100))
                unit.Ct.Value = 100;
        }, () => InBattle);
        DisableBattleOptions = new RelayCommand(() =>
        {
            lockHp = infiniteMp = guestLockHp = freezeCountdown = lockCt = alwaysMove = alwaysAct = maxMoveJump = instantCast = false;
            experienceMultiplier = jpMultiplier = 1;
            SetBattleOptions();
        });
        // 道具筛选用和语言无关的代码，与 InventoryItemViewModel.GroupKey 对应
        ShowAllCategories = new RelayCommand(() => ItemCategory = "all");
        ShowConsumables = new RelayCommand(() => ItemCategory = "consumable");
        ShowEquipment = new RelayCommand(() => ItemCategory = "equipment");
        ShowBonusItems = new RelayCommand(() => ItemCategory = "bonus");
        ShowPoaching = new RelayCommand(() => ItemCategory = "poaching");
        MaxVisibleItems = new RelayCommand(() => BatchRefresh(() =>
        {
            foreach (var item in VisibleItems.ToArray())
                item.Quantity.Value = 99;
        }), () => CanEdit);
        Watch(Gil);
        Undo = new RelayCommand(() => BatchRefresh(() =>
        {
            foreach (var field in Fields)
                field.Revert();
            foreach (var unit in Units) unit.Learning.Reset();
            Status = Localizer.T("core.status.reverted");
            Notify(nameof(Status));
        }), () => !busy && HasChanges);
        if (preferencesPath != null && BattlePreferences.Load(preferencesPath) is { Keep: true } saved)
        {
            savedPreferences = saved;
            keepBattleOptions = true;
            (lockHp, infiniteMp, guestLockHp, freezeCountdown, lockCt) = (saved.LockHp, saved.InfiniteMp, saved.GuestLockHp, saved.FreezeCountdown, saved.LockCt);
            (alwaysMove, alwaysAct, maxMoveJump, instantCast) = (saved.AlwaysMove, saved.AlwaysAct, saved.MaxMoveJump, saved.InstantCast);
            (experienceMultiplier, jpMultiplier) = (saved.ExperienceMultiplier, saved.JpMultiplier);
            SetBattleOptions();
        }
    }

    // Snapshot/Max/Undo update many fields synchronously. Keep stable row identities and
    // reconcile each dependent collection only once, including on an exceptional exit.
    private void BatchRefresh(Action action)
    {
        refreshDepth++;
        try { action(); }
        finally
        {
            if (--refreshDepth == 0)
            {
                if (itemsRefreshRequested)
                {
                    itemsRefreshRequested = false;
                    RefreshItems();
                }
                if (pendingRefreshRequested)
                {
                    pendingRefreshRequested = false;
                    RefreshPending();
                }
            }
        }
    }

    private void RefreshItems()
    {
        if (refreshDepth > 0)
        {
            itemsRefreshRequested = true;
            return;
        }
        var matches = Items.Where(item => (ShowAllItems || item.Quantity.Value > 0)
            && (ItemCategory == "all" || item.GroupKey == ItemCategory)
            && (item.Name.Contains(ItemSearch, StringComparison.OrdinalIgnoreCase)
                || item.HexId.Contains(ItemSearch, StringComparison.OrdinalIgnoreCase))).ToArray();
        foreach (var item in VisibleItems.Except(matches).ToArray())
            VisibleItems.Remove(item);
        for (var i = 0; i < matches.Length; i++)
            if (i >= VisibleItems.Count || VisibleItems[i] != matches[i])
                VisibleItems.Insert(i, matches[i]);
    }

    private void Watch(EditableField field)
    {
        field.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditableField.SecondaryHack) or nameof(EditableField.LearningHack))
            {
                RefreshPending();
                return;
            }
            if (e.PropertyName != nameof(EditableField.IsDirty))
                return;
            RefreshPending();
            if (field.IsDirty && !busy)
            {
                Status = Localizer.T("core.status.staged");
                Notify(nameof(Status));
            }
        };
    }

    // 切换界面语言后调用：重新生成所有带文字的属性。子 VM 只换文字，不重建，待写入的改动原样保留；
    // 已经显示在状态栏的旧提示保持原语言，下一条提示起用新语言。
    public void RefreshLanguage()
    {
        // 游戏文本（单位名、职业、道具、技能）也换成新语言的官方译名
        gameText?.Load(Localizer.Instance.Language);
        RefreshText();
    }

    private void RefreshText()
    {
        Gil.RefreshText();
        foreach (var unit in Units)
            unit.RefreshLanguage();
        foreach (var item in Items)
            item.RefreshLanguage();
        foreach (var unit in Allies.Concat(Guests).Concat(Enemies))
            unit.RefreshLanguage();
        foreach (var field in Pending)
            field.RefreshText();
        RefreshItems(); // Names can change while a search is active.
        Notify(string.Empty);
    }

    private void RefreshPending()
    {
        if (refreshDepth > 0)
        {
            pendingRefreshRequested = true;
            return;
        }
        var pending = Fields.Where(field => field.IsDirty && field.Key is not ("Jp" or "TotalJp" or "JobLevels"))
            .Concat(Units.SelectMany(unit => unit.Jobs).Where(job => job.IsDirty).Select(job => job.Jp)).ToArray();
        var pendingSet = pending.ToHashSet();
        foreach (var field in Pending.Where(field => !pendingSet.Contains(field)).ToArray())
            Pending.Remove(field);
        var existing = Pending.ToHashSet();
        foreach (var field in pending)
            if (!existing.Contains(field))
                Pending.Add(field);
        if (lastHasChanges != HasChanges)
        {
            lastHasChanges = HasChanges;
            Notify(nameof(HasChanges));
        }
        if (lastHasJpChanges != HasJpChanges)
        {
            lastHasJpChanges = HasJpChanges;
            Notify(nameof(HasJpChanges));
        }
        if (lastNeedsConfirmation != NeedsConfirmation)
        {
            lastNeedsConfirmation = NeedsConfirmation;
            Notify(nameof(NeedsConfirmation));
        }
        Undo.Refresh();
    }

    public void ApplySnapshot(GameSnapshot? snapshot) => BatchRefresh(() => ApplySnapshotCore(snapshot));

    private void ApplySnapshotCore(GameSnapshot? snapshot)
    {
        if (busy)
            return;
        connected = snapshot != null;
        // 连上游戏后按进程路径定位游戏目录；目录变了（或第一次找到）就重读游戏文本，已有的名称在最后统一刷新
        var gameTextChanged = gameText?.SetGameRootFromExecutable(snapshot?.ModulePath) == true;
        if (gameTextChanged)
            gameText!.Load(Localizer.Instance.Language);
        Notify(nameof(IsConnected));
        Notify(nameof(CanEdit));
        Notify(nameof(ConnectionText));
        MaxVisibleItems.Refresh();
        RefreshBattle(snapshot);
        if (snapshot == null || generation != snapshot.Generation)
            foreach (var unit in Units)
                unit.ResetSkillHacks();
        if (snapshot == null)
            return;
        if (generation != snapshot.Generation)
        {
            Units.Clear();
            Items.Clear();
            itemsById.Clear();
            VisibleItems.Clear();
            Pending.Clear();
            Gil.IsEditing = false;
            Gil.Revert();
            generation = snapshot.Generation;
            SelectedUnit = null;
            Status = Localizer.T("core.status.staged");
            Notify(nameof(Status));
        }
        Gil.Refresh(snapshot.Gil);
        void RefreshQuantities(byte[] quantities, bool poaching)
        {
            var count = Math.Min(quantities.Length, poaching ? FftLayout.PoachingCount : FftLayout.InventoryCount);
            for (var id = 0; id < count; id++)
            {
                if (itemsById.TryGetValue((id, poaching), out var item))
                {
                    item.Quantity.Refresh(quantities[id]);
                    continue;
                }
                item = new InventoryItemViewModel(id, quantities[id], text, poaching);
                Watch(item.Quantity);
                item.Quantity.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(EditableField.Value))
                        RefreshItems();
                };
                itemsById.Add((id, poaching), item);
                Items.Add(item);
                RefreshItems();
            }
        }
        RefreshQuantities(snapshot.Inventory, false);
        RefreshQuantities(snapshot.Poaching ?? [], true);
        foreach (var removed in Units.Where(unit => snapshot.Units.All(item => item.Slot != unit.Slot)).ToArray())
        {
            removed.ResetSkillHacks();
            foreach (var field in removed.Fields)
                Pending.Remove(field);
            Units.Remove(removed);
        }
        foreach (var item in snapshot.Units)
        {
            var unit = Units.FirstOrDefault(unit => unit.Slot == item.Slot);
            if (unit == null)
            {
                unit = new UnitViewModel(item, layout, text);
                foreach (var slot in unit.SkillSlots)
                    slot.Generation = generation;
                foreach (var field in unit.Fields)
                    Watch(field);
                Units.Add(unit);
            }
            else
                unit.Refresh(item, layout);
        }
        if (SelectedUnit == null || !Units.Contains(SelectedUnit))
            SelectedUnit = Units.FirstOrDefault();
        RefreshPending();
        Notify(nameof(RosterCountText));
        Undo.Refresh();
        if (gameTextChanged)
            RefreshText();
    }

    public bool StageLearning(UnitViewModel unit)
    {
        if (!CanEdit || SelectedUnit != unit || !Units.Contains(unit) || !unit.Learning.Stage(generation)) return false;
        foreach (var field in unit.Learning.Fields)
        {
            Watch(field);
            field.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(EditableField.IsDirty)) unit.RefreshDirty(); };
        }
        unit.RefreshDirty();
        RefreshPending();
        return true;
    }

    public async Task WriteAsync(bool dangerConfirmed = false, IReadOnlyList<MemoryEdit>? confirmedEdits = null)
    {
        if (confirmedEdits != null && !confirmedEdits.SequenceEqual(Pending.Select(field => field.ToEdit())))
            return;
        if (!CanEdit || !HasChanges || NeedsConfirmation && !dangerConfirmed)
            return;
        if (Units.Any(unit => unit.StatRules == null && unit.Stats.Any(stat => stat.Raw.IsDirty)))
        {
            Status = Localizer.T("core.status.statTableFailed");
            Notify(nameof(Status));
            return;
        }
        if (Units.Any(unit => !unit.Learning.ValidPending))
        {
            Status = Localizer.T("core.learn.invalid");
            Notify(nameof(Status));
            return;
        }
        if (Units.SelectMany(unit => unit.SkillSlots).Any(slot => !slot.ValidPending))
        {
            Status = Localizer.T("core.status.skillChanged");
            Notify(nameof(Status));
            return;
        }
        busy = true;
        Notify(nameof(CanEdit));
        Notify(nameof(InBattle));
        Notify(nameof(CanSetBattleOptions));
        Undo.Refresh();
        var fields = Fields.Where(field => field.IsDirty)
            .Concat(Units.Where(unit => unit.Level.IsDirty && unit.StatRules != null)
                .SelectMany(unit => unit.Stats.Select(stat => stat.Raw)))
            .Concat(Units.SelectMany(unit => unit.Jobs).Where(job => job.IsDirty && job.HasGrant)
                .SelectMany(job => new[] { job.Jp, job.Total, job.LevelByte }))
            .Distinct().ToArray();
        var edits = fields.Select(field => field.ToEdit()).ToArray();
        string? backupWarning = null;
        var writeGeneration = generation;
        try
        {
            backupWarning = await Task.Run(() => backups?.EnsureBackup());
            Notify(nameof(BackupText));
            var results = await Task.Run(() => session.Write(writeGeneration, edits));
            foreach (var result in results)
            {
                var field = fields.Single(field => field.Offset == result.Edit.Offset);
                if (result.Error == null)
                {
                    field.Accept();
                    field.Error = result.Warning;
                }
                else
                    field.Error = result.Error;
            }
            foreach (var job in Units.SelectMany(unit => unit.Jobs).Where(job => job.IsDirty))
            {
                var errors = new[] { job.Jp.Error, job.Total.Error, job.LevelByte.Error }.Where(error => error != null);
                job.Jp.Error = errors.Any() ? string.Join("；", errors) : null;
            }
            Status = Localizer.T(results.All(result => result.Error == null) ? "core.status.written" : "core.status.partialFailed");
            var warnings = results.Where(result => result.Warning != null)
                .Select(result => Localizer.F("core.status.warningLine", fields.Single(field => field.Offset == result.Edit.Offset).Label, result.Warning));
            if (warnings.Any())
                Status += " " + string.Join("；", warnings);
        }
        catch (Exception ex)
        {
            foreach (var field in fields)
                field.Error = ex.Message;
            Status = Localizer.T("core.status.writeFailed");
        }
        finally
        {
            if (backupWarning != null)
                Status += " " + backupWarning;
            busy = false;
            Notify(nameof(CanEdit));
            Notify(nameof(InBattle));
        Notify(nameof(CanSetBattleOptions));
            Notify(nameof(Status));
            Undo.Refresh();
        }
    }
}
