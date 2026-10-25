namespace FftTrainer.Core;

public sealed record LearningAction(int Position, int Id, bool Learned);

// Only action positions 0..15. The third byte (passives/reserved bits) is evidence,
// never a write target. Fingerprint covers the live job and command tables.
public sealed record SkillLearningPlan(int Group, int Index, ushort Before, byte Passive,
    ushort Mask, string TableFingerprint, IReadOnlyList<LearningAction> Actions)
{
    public ushort After => (ushort)(Before | Mask);
    public int Offset => FftLayout.LearnedSkills + Index * 3;
    public int LearnedCount => Actions.Count(action => action.Learned);
    public int AddedCount => Actions.Count - LearnedCount;
    public bool SameState(SkillLearningPlan? other) => other != null && Group == other.Group
        && Index == other.Index && Before == other.Before && Passive == other.Passive
        && Mask == other.Mask && TableFingerprint == other.TableFingerprint;
}

// One preview, one attempt. No public constructor: a UI staging operation issues
// this capability, and every target/value is recomputed from live tables at write.
public sealed class SkillLearningAuthorization
{
    internal ulong UnitOffset { get; }
    internal SkillIdentity Identity { get; }
    internal long Generation { get; }
    internal SkillLearningPlan Plan { get; }
    internal bool Active { get; private set; } = true;

    internal SkillLearningAuthorization(ulong unitOffset, SkillIdentity identity, long generation, SkillLearningPlan plan)
        => (UnitOffset, Identity, Generation, Plan) = (unitOffset, identity, generation, plan);

    internal void Revoke() { lock (this) Active = false; }

    internal MemoryEdit[] Edits() => Enumerable.Range(0, 2)
        .Where(i => ((Plan.Before ^ Plan.After) & (0xFF << (8 * i))) != 0)
        .Select(i => new MemoryEdit(UnitOffset + (ulong)(Plan.Offset + i), 1,
            (uint)((Plan.Before >> (8 * i)) & 255), (uint)((Plan.After >> (8 * i)) & 255), Identity,
            LearningHack: this)).ToArray();
}

internal static class SkillLearningWrites
{
    // The generic editor must not become a back door into ANY learned byte,
    // including passive bytes, unused k=20/21 and direct mirror addresses.
    internal static bool TouchesLearned(MemoryEdit edit, FftLayout layout)
    {
        if (edit.Width <= 0) return false;
        var end = checked(edit.Offset + (ulong)edit.Width);
        foreach (var roster in new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors))
        {
            if (end <= roster || edit.Offset >= roster + (ulong)(layout.RosterStride * layout.RosterCount)) continue;
            for (var i = 0; i < layout.RosterCount; i++)
            {
                var start = roster + (ulong)(i * layout.RosterStride + FftLayout.LearnedSkills);
                if (edit.Offset < start + FftLayout.JobCount * 3 && end > start) return true;
            }
        }
        return false;
    }

    internal static IReadOnlyList<WriteResult> Apply(IMemorySource source, Action<ulong, byte[]> write,
        ulong moduleBase, IReadOnlyList<MemoryEdit> allEdits, FftLayout layout, long? generation)
    {
        var results = new List<WriteResult>();
        foreach (var group in allEdits.Where(edit => edit.LearningHack != null).GroupBy(edit => edit.LearningHack!))
        {
            var authorization = group.Key;
            var edits = group.ToArray();
            lock (authorization)
            {
                string? error = null;
                try
                {
                    var expected = authorization.Edits();
                    if (!authorization.Active || generation != authorization.Generation || expected.Length == 0
                        || !edits.OrderBy(edit => edit.Offset).SequenceEqual(expected.OrderBy(edit => edit.Offset))
                        || authorization.UnitOffset < FftLayout.Roster
                        || authorization.UnitOffset - FftLayout.Roster >= (ulong)(layout.RosterCount * layout.RosterStride)
                        || (authorization.UnitOffset - FftLayout.Roster) % (ulong)layout.RosterStride != 0
                        || allEdits.Any(edit => new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors).Any(roster =>
                        {
                            var secondary = roster + authorization.UnitOffset - FftLayout.Roster + FftLayout.SecondarySkill;
                            return edit.Width > 0 && edit.Offset <= secondary && secondary - edit.Offset < (ulong)edit.Width;
                        })))
                        throw new IOException(Localizer.T("core.learn.invalid"));
                    var tables = SkillTables.TryLoad(source, moduleBase)
                        ?? throw new IOException(Localizer.T("core.learn.invalid"));
                    var unitRelative = authorization.UnitOffset - FftLayout.Roster;
                    var rosters = new[] { FftLayout.Roster }.Concat(FftLayout.RosterMirrors).ToArray();
                    // Preflight all identities, equipped commands and original learned
                    // bytes before touching any of the three rosters.
                    foreach (var roster in rosters)
                    {
                        var unit = source.ReadBytes(moduleBase + roster + unitRelative, FftLayout.UnitSize);
                        if (SkillIdentity.Read(unit) != authorization.Identity
                            || !authorization.Plan.SameState(tables.ReadUnit(unit)?.Learning))
                            throw new IOException(Localizer.T("core.learn.invalid"));
                    }
                    var targets = edits.SelectMany(edit => rosters.Select(roster =>
                        (Edit: edit, Address: moduleBase + roster + edit.Offset - FftLayout.Roster))).ToArray();
                    var errors = new List<string>();
                    foreach (var target in targets)
                    {
                        try { write(target.Address, [(byte)target.Edit.After]); }
                        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
                        {
                            errors.Add(ex.Message);
                            break; // Partial writes are possible. Never retry/rebase silently.
                        }
                    }
                    foreach (var target in targets)
                    {
                        try
                        {
                            if (source.ReadBytes(target.Address, 1)[0] != target.Edit.After)
                                errors.Add(Localizer.F("core.error.readbackMismatch", target.Address - moduleBase));
                        }
                        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
                        { errors.Add(ex.Message); }
                    }
                    if (errors.Count != 0) error = Localizer.T("core.learn.partial") + " " + string.Join("; ", errors);
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
                { error = ex.Message; }
                finally { authorization.Revoke(); }
                results.AddRange(edits.Select(edit => new WriteResult(edit, error,
                    error == null ? Localizer.T("core.learn.writeWarning") : null)));
            }
        }
        return results;
    }
}
