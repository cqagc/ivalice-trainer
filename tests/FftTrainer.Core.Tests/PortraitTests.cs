using System.Buffers.Binary;
using System.IO.Compression;
using FftTrainer.Core;
using FftTrainer.GameFiles;
using Xunit;

namespace FftTrainer.Core.Tests;

public sealed class PortraitTests
{
    [Fact]
    public void PngWriterProducesValidRgbaPng()
    {
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0, 10, 20, 30, 40];
        var png = PngWriter.Encode(rgba, 2, 2);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        var chunks = new List<(string Type, byte[] Data)>();
        for (var at = 8; at < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length).ToArray();
            var crc = new System.IO.Hashing.Crc32();
            crc.Append(png.AsSpan(at + 4, 4 + length));
            Assert.Equal(crc.GetCurrentHashAsUInt32(), BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length)));
            chunks.Add((type, data));
            at += 12 + length;
        }
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, chunks.Select(chunk => chunk.Type));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(chunks[0].Data));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(chunks[0].Data.AsSpan(4)));
        Assert.Equal(new byte[] { 8, 6, 0, 0, 0 }, chunks[0].Data[8..]);
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(chunks[1].Data), CompressionMode.Decompress))
            zlib.CopyTo(inflated);
        Assert.Equal(new byte[] { 0, 255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 0, 255, 0, 10, 20, 30, 40 }, inflated.ToArray());
    }

    [Fact]
    public void PngWriterRejectsMismatchedBuffers() =>
        Assert.Throws<ArgumentException>(() => PngWriter.Encode(new byte[15], 2, 2));

    private static byte[] Parts(params (string Name, int X, int Y, int Right, int Bottom)[] parts)
    {
        // header: +4 records start, +8 count; records 20 bytes; names after the records
        const int start = 12;
        var names = new MemoryStream();
        var offsets = new List<int>();
        foreach (var part in parts)
        {
            offsets.Add((int)names.Length);
            names.Write(System.Text.Encoding.UTF8.GetBytes(part.Name));
            names.WriteByte(0);
        }
        var bytes = new byte[start + 20 * parts.Length + names.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), start);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            var record = start + 20 * i;
            var nameAt = start + 20 * parts.Length + offsets[i];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(record), (uint)(nameAt - record));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record + 4), parts[i].X);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record + 8), parts[i].Y);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record + 12), parts[i].Right);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record + 16), parts[i].Bottom);
        }
        names.ToArray().CopyTo(bytes, start + 20 * parts.Length);
        return bytes;
    }

    [Fact]
    public void TexturePartsAreParsedLikeTheScript()
    {
        var parts = PortraitExtractor.ReadTextureParts(Parts(("TexturePart", 0, 0, 520, 388), ("All", 391, 1, 519, 193)));
        Assert.Equal(new TexturePart("All", 391, 1, 519, 193), parts[1]);
        Assert.Equal("TexturePart", parts[0].Name);
    }

    [Fact]
    public void TexturePartsRejectOutOfRangeData()
    {
        Assert.Throws<InvalidDataException>(() => PortraitExtractor.ReadTextureParts(new byte[8]));
        var bytes = Parts(("All", 0, 0, 1, 1));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 50);
        Assert.Throws<InvalidDataException>(() => PortraitExtractor.ReadTextureParts(bytes));
        bytes = Parts(("All", 0, 0, 1, 1));
        bytes[^1] = (byte)'x';   // name without terminator
        Assert.Throws<InvalidDataException>(() => PortraitExtractor.ReadTextureParts(bytes));
    }

    [Fact]
    public void CropCopiesTheAllRectangleAndRejectsOutOfBounds()
    {
        var rgba = Enumerable.Range(0, 4 * 3 * 4).Select(i => (byte)i).ToArray();   // 4x3
        var crop = PortraitExtractor.Crop(rgba, 4, 3, new TexturePart("All", 1, 1, 3, 3));
        Assert.Equal(rgba.AsSpan(20, 8).ToArray().Concat(rgba.AsSpan(36, 8).ToArray()), crop);
        Assert.Throws<InvalidDataException>(() => PortraitExtractor.Crop(rgba, 4, 3, new TexturePart("All", 1, 1, 5, 3)));
        Assert.Throws<InvalidDataException>(() => PortraitExtractor.Crop(rgba, 4, 3, new TexturePart("All", 2, 2, 2, 3)));
    }

    [Fact]
    public void MappingsMatchTheExtractionScript()
    {
        var mappings = PortraitExtractor.BuildMappings();
        Assert.Equal(93, mappings.Count);
        Assert.Equal(new PortraitMapping("0x01", "0x01", "male", "face/wldface_001_08_uitx.png", null), mappings[0]);
        Assert.Equal(38, mappings.Count(mapping => mapping.CharacterCode is "0x80" or "0x81"));
        Assert.Contains(new PortraitMapping("0x81", "0x4B", "female", "face/wldface_099_08_uitx.png", "job/jv_02_f_uitx.png"), mappings);
        // Bard 0x5B male only, Dancer 0x5C female only, Mime 0x5D both
        Assert.Single(mappings, mapping => mapping.JobId == "0x5B");
        Assert.Equal("0x80", mappings.Single(mapping => mapping.JobId == "0x5B").CharacterCode);
        Assert.Equal("face/wldface_131_08_uitx.png", mappings.Single(mapping => mapping.JobId == "0x5C").Face);
        Assert.Equal(2, mappings.Count(mapping => mapping.JobId == "0x5D"));
        var monsters = mappings.Where(mapping => mapping.Gender == "monster").ToDictionary(mapping => mapping.JobId, mapping => mapping.Face);
        Assert.Equal(54, monsters.Count);
        Assert.Equal("monster/wldface_154_08_uitx.png", monsters["0x91"]);   // Construct 8
        // PortraitCatalog resolves monster faces only from monster/
        Assert.All(monsters.Values, face => Assert.StartsWith("monster/", face));
        Assert.Equal("monster/wldface_134_08_uitx.png", monsters["0x5E"]);
        Assert.Equal("monster/wldface_149_10_uitx.png", monsters["0x8D"]);
        Assert.Equal(monsters["0x67"], monsters["0xA9"]);
        Assert.Equal(monsters["0x5E"], monsters["0xAD"]);
    }

    [Fact]
    public void SpecialFacesUseOwnFaceThenSameNamedAliasAndOverrides()
    {
        var names = new Dictionary<int, string>
        {
            [0x0D] = "Orlandeau", [0x1E] = "Agrias", [0x34] = "Agrias", [0x2A] = "Meliadoul", [0x2F] = "Meliadoul",
            [0x16] = "Mustadio", [0x22] = "Mustadio", [0x2D] = "Celia", [0x10] = ""
        };
        var faces = new HashSet<int> { 13, 30, 34, 47, 52, 120 };
        names[0x78] = "Lavian";
        var special = PortraitExtractor.BuildSpecialFaces(names, faces);
        Assert.Equal(13, special[0x0D]);
        Assert.Equal(52, special[0x1E]);   // verified override: face 30 is not Agrias
        Assert.Equal(52, special[0x34]);
        Assert.Equal(47, special[0x2A]);
        Assert.Equal(34, special[0x16]);
        Assert.False(special.ContainsKey(0x2D));
        Assert.False(special.ContainsKey(0x10));
        Assert.False(special.ContainsKey(0x78));   // face 120 is a generic job face, not Lavian
    }

    [Fact]
    public void CatalogUsesSpecialFacesInsteadOfGuessingByCharacterCode()
    {
        const string json = """
            {"rosterMappings":[],"specialFaces":[{"characterCode":"0x2A","face":"face/wldface_047_08_uitx.png"}]}
            """;
        var catalog = PortraitCatalog.Load("exe", "local", path => path.StartsWith(Path.Combine("local", "FftTrainer"))
            ? json : throw new IOException());
        var root = Path.Combine("local", "FftTrainer", "portraits");
        var paths = catalog.Resolve(new PortraitIdentity(0x2A, 0x4B, "female"), _ => true);
        Assert.Equal(Path.Combine(root, "face", "wldface_047_08_uitx.png"), paths.Face);
        // No special entry and the cache has specialFaces: do not guess wldface_{code}
        var none = catalog.Resolve(new PortraitIdentity(0x2B, 0x4B, "male"), path => path.StartsWith(root));
        Assert.Null(none.Face);
    }

    [Fact]
    public void CatalogResolvesConstruct8FromTheGeneratedMapping()
    {
        var mapping = PortraitExtractor.BuildMappings().Single(entry => entry.JobId == "0x91");
        var json = $$"""{"rosterMappings":[{"characterCode":"{{mapping.CharacterCode}}","jobId":"{{mapping.JobId}}","gender":"{{mapping.Gender}}","face":"{{mapping.Face}}","jobIllustration":null}],"specialFaces":[]}""";
        var catalog = PortraitCatalog.Load("exe", "local", path => path.StartsWith(Path.Combine("local", "FftTrainer"))
            ? json : throw new IOException());
        var paths = catalog.Resolve(new PortraitIdentity(0x82, 0x91, "monster"), _ => true);
        Assert.Equal(Path.Combine("local", "FftTrainer", "portraits", "monster", "wldface_154_08_uitx.png"), paths.Face);
    }

    [Fact]
    public async Task CacheSkipsUpToDateExtractsOnceAndLogsFailures()
    {
        var extracts = 0;
        var updates = 0;
        var logs = new List<string>();
        var upToDate = false;
        var cache = new PortraitCache("out", logs.Add, (_, _) => upToDate, (_, _) =>
        {
            extracts++;
            return new PortraitExtractionResult(352, 38, 48, 92, 0, TimeSpan.FromSeconds(1));
        });
        cache.Updated += () => updates++;
        await cache.Ensure(null);
        await cache.Ensure("game");
        await cache.Ensure("GAME");
        Assert.Equal(1, extracts);
        Assert.Equal(1, updates);
        Assert.Contains(logs, line => line.Contains("faces=352"));
        upToDate = true;
        await cache.Ensure("other");
        Assert.Equal(1, extracts);

        var failing = new PortraitCache("out", logs.Add, (_, _) => false, (_, _) => throw new IOException("disk full"));
        failing.Updated += () => updates++;
        await failing.Ensure("game");
        Assert.Equal(1, updates);
        Assert.Contains(logs, line => line.Contains("disk full"));
    }

    [Fact]
    public void FingerprintDecidesWhetherTheCacheIsCurrent()
    {
        var dir = Directory.CreateTempSubdirectory("fft-portraits-").FullName;
        try
        {
            var game = Directory.CreateDirectory(Path.Combine(dir, "game", "data", "enhanced")).Parent!.Parent!.FullName;
            var output = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
            Assert.Null(PortraitExtractor.Fingerprint(game));
            Assert.False(PortraitExtractor.IsUpToDate(game, output));
            File.WriteAllBytes(PortraitExtractor.PackPath(game), [1, 2, 3]);
            var fingerprint = PortraitExtractor.Fingerprint(game)!;
            File.WriteAllText(Path.Combine(output, "portraits.json"), $$"""{"sourceFingerprint":"{{fingerprint}}"}""");
            Assert.True(PortraitExtractor.IsUpToDate(game, output));
            File.WriteAllBytes(PortraitExtractor.PackPath(game), [1, 2, 3, 4]);
            Assert.False(PortraitExtractor.IsUpToDate(game, output));
            File.WriteAllText(Path.Combine(output, "portraits.json"), "not json");
            Assert.False(PortraitExtractor.IsUpToDate(game, output));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FailedExtractionKeepsTheOldCacheAndNeverWritesIntoTheGame()
    {
        var dir = Directory.CreateTempSubdirectory("fft-portraits-").FullName;
        try
        {
            var game = Directory.CreateDirectory(Path.Combine(dir, "game")).FullName;
            var output = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
            File.WriteAllText(Path.Combine(output, "portraits.json"), "old");
            Assert.Throws<FileNotFoundException>(() => PortraitExtractor.Extract(game, output));
            Assert.Equal("old", File.ReadAllText(Path.Combine(output, "portraits.json")));
            Directory.CreateDirectory(Path.Combine(game, "data", "enhanced"));
            File.WriteAllBytes(PortraitExtractor.PackPath(game), [1]);
            Assert.Throws<InvalidOperationException>(() => PortraitExtractor.Extract(game, Path.Combine(game, "cache")));
            Assert.False(Directory.Exists(Path.Combine(game, "cache")));
            Assert.Empty(Directory.GetDirectories(dir, ".portraits-*"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
