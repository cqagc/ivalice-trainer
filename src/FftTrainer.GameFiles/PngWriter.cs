using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace FftTrainer.GameFiles;

// 最小 PNG 编码器：8 位 RGBA、不隔行、每行滤波类型 0。只给运行时立绘缓存用，不依赖 WPF / System.Drawing。
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
            throw new ArgumentException("RGBA buffer does not match the image size.");
        using var output = new MemoryStream();
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // RGBA
        WriteChunk(output, "IHDR", header);
        using (var raw = new MemoryStream())
        {
            using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                var stride = width * 4;
                for (var y = 0; y < height; y++)
                {
                    zlib.WriteByte(0);
                    zlib.Write(rgba.Slice(y * stride, stride));
                }
            }
            WriteChunk(output, "IDAT", raw.GetBuffer().AsSpan(0, (int)raw.Length));
        }
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc.GetCurrentHashAsUInt32());
        output.Write(buffer);
    }
}
