using System.Buffers.Binary;
using System.Globalization;

namespace FftTrainer.Core;

public enum ValueTypeCode : byte
{
    U8, U16, U32, I32, U64, F32, U24
}

public static class Values
{
    public static ValueTypeCode ParseType(string text) => text.ToLowerInvariant() switch
    {
        "u8" => ValueTypeCode.U8,
        "u16" => ValueTypeCode.U16,
        "u24" => ValueTypeCode.U24,
        "u32" => ValueTypeCode.U32,
        "i32" => ValueTypeCode.I32,
        "u64" => ValueTypeCode.U64,
        "f32" => ValueTypeCode.F32,
        _ => throw new FormatException("Type must be u8/u16/u24/u32/i32/u64/f32.")
    };

    public static int Size(ValueTypeCode type) => type switch
    {
        ValueTypeCode.U8 => 1,
        ValueTypeCode.U16 => 2,
        ValueTypeCode.U24 => 3,
        ValueTypeCode.U32 or ValueTypeCode.I32 or ValueTypeCode.F32 => 4,
        ValueTypeCode.U64 => 8,
        _ => throw new FormatException("Invalid value type.")
    };

    public static ulong Bits(ReadOnlySpan<byte> bytes, ValueTypeCode type) => Size(type) switch
    {
        1 => bytes[0],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
        3 => (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
        _ => BinaryPrimitives.ReadUInt64LittleEndian(bytes)
    };

    public static ulong Parse(string text, ValueTypeCode type)
    {
        var culture = CultureInfo.InvariantCulture;
        if (type == ValueTypeCode.F32)
            return BitConverter.SingleToUInt32Bits(float.Parse(text, culture));
        if (type == ValueTypeCode.I32)
            return unchecked((uint)int.Parse(text, culture));
        var value = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(text[2..], NumberStyles.HexNumber, culture) : ulong.Parse(text, culture);
        if (Size(type) < 8 && value >= (1UL << (Size(type) * 8)))
            throw new OverflowException("Value exceeds type range.");
        return value;
    }

    public static byte[] ToBytes(ulong bits, ValueTypeCode type)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, bits);
        return bytes[..Size(type)];
    }

    public static string Format(ulong bits, ValueTypeCode type) => type switch
    {
        ValueTypeCode.F32 => BitConverter.UInt32BitsToSingle((uint)bits).ToString("R", CultureInfo.InvariantCulture),
        ValueTypeCode.I32 => unchecked((int)bits).ToString(CultureInfo.InvariantCulture),
        _ => bits.ToString(CultureInfo.InvariantCulture)
    };

    public static void ValidateOperation(string op)
    {
        if (op is not ("eq" or "ne" or "changed" or "unchanged" or "increased" or "decreased" or "gt" or "lt"))
            throw new ArgumentException("Unknown comparison operation.");
    }

    public static bool Compare(ValueTypeCode type, ulong current, ulong previous, string op, ulong target = 0)
    {
        ValidateOperation(op);
        var other = op is "changed" or "unchanged" or "increased" or "decreased" ? previous : target;
        int comparison;
        if (type == ValueTypeCode.F32)
        {
            var a = BitConverter.UInt32BitsToSingle((uint)current);
            var b = BitConverter.UInt32BitsToSingle((uint)other);
            if (float.IsNaN(a) || float.IsNaN(b))
                return op is "ne" or "changed";
            comparison = a.CompareTo(b);
        }
        else if (type == ValueTypeCode.I32)
            comparison = unchecked((int)current).CompareTo(unchecked((int)other));
        else
            comparison = current.CompareTo(other);
        return op switch
        {
            "eq" or "unchanged" => comparison == 0,
            "ne" or "changed" => comparison != 0,
            "gt" or "increased" => comparison > 0,
            _ => comparison < 0
        };
    }
}

public readonly record struct ScanResult(ulong Address, ulong Previous);

public static class ResultFile
{
    private const uint Magic = 0x31544646;

    public static BinaryWriter Create(string path, ValueTypeCode type)
    {
        _ = Values.Size(type);
        var writer = new BinaryWriter(File.Create(path));
        writer.Write(Magic);
        writer.Write((byte)type);
        return writer;
    }

    public static ValueTypeCode ReadHeader(BinaryReader reader)
    {
        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Invalid session file.");
        var type = (ValueTypeCode)reader.ReadByte();
        _ = Values.Size(type);
        if ((reader.BaseStream.Length - reader.BaseStream.Position) % 16 != 0)
            throw new InvalidDataException("Truncated session file.");
        return type;
    }

    public static void Write(BinaryWriter writer, ScanResult result)
    {
        writer.Write(result.Address);
        writer.Write(result.Previous);
    }

    public static IEnumerable<ScanResult> Read(BinaryReader reader)
    {
        while (reader.BaseStream.Position < reader.BaseStream.Length)
            yield return new ScanResult(reader.ReadUInt64(), reader.ReadUInt64());
    }
}

public sealed class ValueScanner(IMemorySource source, int blockSize = 1024 * 1024)
{
    private static long Save(string path, ValueTypeCode type, IEnumerable<ScanResult> results)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            long count = 0;
            using (var writer = ResultFile.Create(temporary, type))
            {
                foreach (var result in results)
                {
                    ResultFile.Write(writer, result);
                    count++;
                }
            }
            File.Move(temporary, path, true);
            return count;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public long Scan(string path, ValueTypeCode type, ulong target)
    {
        IEnumerable<ScanResult> Results()
        {
            foreach (var block in MemoryChunks.Read(source, source.GetRegions(), Values.Size(type), blockSize))
            {
                for (var i = 0; i < block.CandidateCount; i++)
                {
                    var value = Values.Bits(block.Bytes.AsSpan(i), type);
                    if (Values.Compare(type, value, 0, "eq", target))
                        yield return new ScanResult(block.Address + (ulong)i, value);
                }
            }
        }
        return Save(path, type, Results());
    }

    public long Next(string path, string op, ulong target = 0)
    {
        Values.ValidateOperation(op);
        ValueTypeCode type;
        using (var header = new BinaryReader(File.OpenRead(path)))
            type = ResultFile.ReadHeader(header);
        IEnumerable<ScanResult> Results()
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            _ = ResultFile.ReadHeader(reader);
            byte[]? cache = null;
            ulong cacheStart = 0;
            foreach (var result in ResultFile.Read(reader))
            {
                var width = Values.Size(type);
                if (cache == null || result.Address < cacheStart
                    || result.Address - cacheStart > (ulong)Math.Max(0, cache.Length - width))
                {
                    cacheStart = result.Address;
                    cache = null;
                    try
                    {
                        cache = source.ReadBytes(cacheStart, Math.Max(width, blockSize));
                    }
                    catch (IOException)
                    {
                        try
                        {
                            cache = source.ReadBytes(cacheStart, Math.Max(width, 4096 - (int)(cacheStart % 4096)));
                        }
                        catch (IOException)
                        {
                            try
                            {
                                cache = source.ReadBytes(cacheStart, width);
                            }
                            catch (IOException)
                            {
                                // A stale address is removed from the next result set.
                            }
                        }
                    }
                }
                if (cache == null)
                    continue;
                var value = Values.Bits(cache.AsSpan((int)(result.Address - cacheStart)), type);
                if (Values.Compare(type, value, result.Previous, op, target))
                    yield return new ScanResult(result.Address, value);
            }
        }
        return Save(path, type, Results());
    }
}

public readonly record struct SequenceField(ValueTypeCode Type, ulong Value, int Offset)
{
    public static SequenceField Parse(string text)
    {
        var colon = text.IndexOf(':');
        var at = text.IndexOf('@');
        if (colon < 1 || at <= colon + 1)
            throw new FormatException("Expected type:value@offset.");
        var type = Values.ParseType(text[..colon]);
        var offset = int.Parse(text[(at + 1)..], CultureInfo.InvariantCulture);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return new SequenceField(type, Values.Parse(text[(colon + 1)..at], type), offset);
    }
}

public static class SequenceScanner
{
    public static IEnumerable<ulong> Scan(IMemorySource source, IReadOnlyList<SequenceField> fields,
        int blockSize = 1024 * 1024)
    {
        if (fields.Count == 0 || fields.Any(f => f.Offset < 0))
            throw new ArgumentException("At least one field with nonnegative offset is required.");
        var width = fields.Max(f => checked(f.Offset + Values.Size(f.Type)));
        foreach (var block in MemoryChunks.Read(source, source.GetRegions(), width, blockSize))
        {
            for (var i = 0; i < block.CandidateCount; i++)
            {
                var matches = true;
                foreach (var field in fields)
                {
                    if (!Values.Compare(field.Type, Values.Bits(block.Bytes.AsSpan(i + field.Offset), field.Type),
                        0, "eq", field.Value))
                    {
                        matches = false;
                        break;
                    }
                }
                if (matches)
                    yield return block.Address + (ulong)i;
            }
        }
    }
}
