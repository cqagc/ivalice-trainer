using FftTrainer.Core;
using Xunit;
using Xunit.Abstractions;

namespace FftTrainer.Core.Tests;

// 用实战快照回放倍率逻辑；快照文件不入库，不存在时跳过
public sealed class RewardReplayTests(ITestOutputHelper output)
{
    private sealed class Mem : IMemorySource
    {
        public readonly Dictionary<ulong, byte> Bytes = [];
        public IEnumerable<MemoryRegion> GetRegions(bool privateOnly = false, MemoryRegion? range = null) => [];
        public byte[] ReadBytes(ulong address, int length) => Enumerable.Range(0, length)
            .Select(i => Bytes.GetValueOrDefault(address + (ulong)i)).ToArray();
        public void Load(byte[] data) { for (var i = 0; i < data.Length; i++) Bytes[0x1853CE0 + (ulong)i] = data[i]; }
    }

    [Fact]
    public void ReplayStoryBattleKnightAttack()
    {
        // 快照目录由环境变量 FFT_REPLAY_DIR 指定（rw-A.bin / rw-B.bin），没设就跳过
        var directory = Environment.GetEnvironmentVariable("FFT_REPLAY_DIR");
        if (string.IsNullOrEmpty(directory))
            return;
        string a = Path.Combine(directory, "rw-A.bin"), b = Path.Combine(directory, "rw-B.bin");
        if (!File.Exists(a) || !File.Exists(b)) return;
        var memory = new Mem();
        memory.Load(File.ReadAllBytes(a));
        var rewards = new BattleRewards();
        rewards.Configure(5, 5);
        var writes = new List<string>();
        void Write(ulong address, byte[] data)
        {
            writes.Add($"0x{address:X} <- {Convert.ToHexString(data)}");
            for (var i = 0; i < data.Length; i++) memory.Bytes[address + (ulong)i] = data[i];
        }
        var units = BattleRules.Read(memory, 0);
        output.WriteLine($"tick1 units={units.Count} inBattle={BattleRules.IsInBattle(units)} players={units.Count(u => u.Side == BattleSide.Player)}");
        rewards.Apply(memory, Write, 0, units);
        output.WriteLine($"after tick1 notice={rewards.Notice} exp={rewards.ExperienceMultiplier} jp={rewards.JpMultiplier}");
        memory.Load(File.ReadAllBytes(b));
        units = BattleRules.Read(memory, 0);
        output.WriteLine($"tick2 units={units.Count} inBattle={BattleRules.IsInBattle(units)}");
        rewards.Apply(memory, Write, 0, units);
        output.WriteLine($"after tick2 notice={rewards.Notice} exp={rewards.ExperienceMultiplier} jp={rewards.JpMultiplier} writes={writes.Count}");
        foreach (var w in writes) output.WriteLine(w);
        Assert.NotEmpty(writes);
    }
}
