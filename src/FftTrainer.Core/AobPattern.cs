using System.Globalization;

namespace FftTrainer.Core;

public sealed class AobPattern
{
    private readonly byte?[] bytes;
    public int Length => bytes.Length;

    public AobPattern(string text)
    {
        bytes = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token is "?" or "??" ? (byte?)null
                : token.Length == 2 ? byte.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : throw new FormatException("Pattern bytes must be two hexadecimal digits or ??."))
            .ToArray();
        if (bytes.Length == 0)
            throw new FormatException("Pattern cannot be empty.");
    }

    public bool Matches(ReadOnlySpan<byte> data)
    {
        if (data.Length < Length)
            return false;
        for (var i = 0; i < Length; i++)
        {
            if (bytes[i] is { } value && data[i] != value)
                return false;
        }
        return true;
    }

    public int FindFirst(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i <= data.Length - Length; i++)
        {
            if (Matches(data[i..]))
                return i;
        }
        return -1;
    }

    public IReadOnlyList<int> FindAll(ReadOnlySpan<byte> data)
    {
        var results = new List<int>();
        for (var i = 0; i <= data.Length - Length; i++)
        {
            if (Matches(data[i..]))
                results.Add(i);
        }
        return results;
    }
}

public static class AobScanner
{
    public static IEnumerable<ulong> Scan(IMemorySource source, AobPattern pattern,
        IEnumerable<MemoryRegion> regions, int blockSize = 1024 * 1024)
    {
        foreach (var block in MemoryChunks.Read(source, regions, pattern.Length, blockSize))
        {
            for (var i = 0; i < block.CandidateCount; i++)
            {
                if (pattern.Matches(block.Bytes.AsSpan(i)))
                    yield return block.Address + (ulong)i;
            }
        }
    }

    public static ulong ResolveRipRelative(IMemorySource source, ulong instrAddr, int dispOffset, int instrLen)
    {
        var displacement = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
            source.ReadBytes(checked(instrAddr + (ulong)dispOffset), 4));
        return checked((ulong)(checked((long)instrAddr + instrLen) + displacement));
    }
}
