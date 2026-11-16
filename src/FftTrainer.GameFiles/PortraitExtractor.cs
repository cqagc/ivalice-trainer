using System.Text.Json;
using System.Text.RegularExpressions;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using FF16Tools.Files.Textures;
using FF16Tools.Pack;
using FF16Tools.Shared.Dds;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("FftTrainer.Core.Tests")]

namespace FftTrainer.GameFiles;

public sealed record TexturePart(string Name, int X, int Y, int Right, int Bottom);

public sealed record PortraitMapping(string CharacterCode, string JobId, string Gender, string Face, string? JobIllustration);

public sealed record PortraitExtractionResult(int Faces, int Jobs, int Monsters, int Mappings, int Skipped, TimeSpan Elapsed);

// 运行时从玩家本机的游戏封包提取立绘（docs/assets-research.md「运行时提取」）。
// 与 tools/extract-portraits/extract-portraits.ps1 产出同结构的 face/、job/、monster/ 和 portraits.json，
// 只是不再需要 FF16Tools CLI 和 texconv：解包用 vendored FF16Pack，纹理用 vendored TextureFile，BC7 用 BCnEncoder.Net。
// 全程只读游戏目录；先写临时目录，全部成功后再替换旧缓存。
public static partial class PortraitExtractor
{
    // 输出结构或映射规则变化时加 1，旧缓存会被重新生成
    public const int SchemaVersion = 4;
    public const string PackRelativePath = "data/enhanced/0008.pac";
    private const string FacePrefix = "ui/ffto/common/face/";
    private const string JobPrefix = "ui/ffto/icon/job_visual/";

    [GeneratedRegex(@"^ui/ffto/(common/face|icon/job_visual)/texture/[^/]+\.tex$")]
    private static partial Regex TexturePath();

    [GeneratedRegex(@"^wldface_(\d{3})_(08|09|10)_uitx$")]
    private static partial Regex MonsterFace();

    [GeneratedRegex(@"^face/wldface_(\d{3})_08_uitx\.png$")]
    private static partial Regex FaceId();

    // 已目视核对：同号头像不是本人的特殊角色码 → 正确头像编号
    // 0x1E 阿格莉亚丝：30 号是戴主教帽的老人，本人（金发辫子）在 52 号
    private static readonly IReadOnlyDictionary<int, int> FaceOverrides = new Dictionary<int, int> { [0x1E] = 52 };

    // 特殊角色（角色码 < 0x80）→ 头像编号。大多数角色码与 wldface 编号相同；没有同号头像时，
    // 游戏 CharaName 表里同名的另一个编号（客座 / 剧情版本）通常有头像，例如美莉娅多 0x2A → 47、
    // 姆斯塔迪奥 0x16 → 34、拉法 0x29 → 25（2026-11-16 全部 352 张头像目视核对）。
    public static IReadOnlyDictionary<int, int> BuildSpecialFaces(IReadOnlyDictionary<int, string> names, IReadOnlySet<int> faces)
    {
        // 96..133 是通用职业头像、134..149 是怪物：角色码落在这个区间时不能按同号取图
        bool Special(int face) => face < 96 && faces.Contains(face);
        int? Own(int code) => FaceOverrides.TryGetValue(code, out var face) ? Special(face) ? face : null
            : Special(code) ? code : null;
        var result = new SortedDictionary<int, int>();
        for (var code = 1; code < 0x80; code++)
        {
            var face = Own(code);
            if (face == null && names.TryGetValue(code, out var name) && !string.IsNullOrWhiteSpace(name))
                face = names.Where(entry => entry.Key != code && entry.Key is > 0 and < 0x80 && entry.Value == name)
                    .Select(entry => Own(entry.Key)).FirstOrDefault(candidate => candidate != null);
            if (face != null)
                result[code] = face.Value;
        }
        return result;
    }

    // 读任一可用语言的 CharaName 表（只用来按名字找别名，语言无关）；读不到就只用同号头像
    private static IReadOnlyDictionary<int, string> ReadCharacterNames(string gameRoot)
    {
        foreach (var language in new[] { "en", "ja", "fr", "de", "ko", "cs", "ct" })
        {
            if (!File.Exists(Path.Combine(GameFileReader.EnhancedDirectory(gameRoot), $"0004.{language}.pac")))
                continue;
            try
            {
                var rows = GameFileReader.ReadLocalizedTables(gameRoot, language, ["charaname"])["charaname"];
                return rows.OrderBy(row => row.Key).GroupBy(row => row.Key)
                    .ToDictionary(group => group.Key, group => group.First().Text("Name") ?? "");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
                or UnauthorizedAccessException or KeyNotFoundException or DllNotFoundException)
            {
                // 换下一种语言
            }
        }
        return new Dictionary<int, string>();
    }

    public static string PackPath(string gameRoot) => Path.Combine(gameRoot, "data", "enhanced", "0008.pac");

    // 来源指纹：封包长度 + 修改时间 + 提取器结构版本
    public static string? Fingerprint(string gameRoot)
    {
        var pack = new FileInfo(PackPath(gameRoot));
        return pack.Exists ? $"{SchemaVersion}|{pack.Length}|{pack.LastWriteTimeUtc.Ticks}" : null;
    }

    public static bool IsUpToDate(string gameRoot, string outputDirectory)
    {
        var expected = Fingerprint(gameRoot);
        if (expected == null)
            return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDirectory, "portraits.json")));
            return document.RootElement.TryGetProperty("sourceFingerprint", out var value)
                && value.GetString() == expected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException)
        {
            return false;
        }
    }

    public static PortraitExtractionResult Extract(string gameRoot, string outputDirectory, CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var fingerprint = Fingerprint(gameRoot) ?? throw new FileNotFoundException("Portrait pack not found.", PackPath(gameRoot));
        var output = Path.GetFullPath(outputDirectory);
        var root = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (output.Equals(root, StringComparison.OrdinalIgnoreCase)
            || output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to write into the game directory.");
        var parent = Path.GetDirectoryName(output.TrimEnd(Path.DirectorySeparatorChar))!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".portraits-{Guid.NewGuid():N}");
        try
        {
            var faceDir = Directory.CreateDirectory(Path.Combine(staging, "face")).FullName;
            var jobDir = Directory.CreateDirectory(Path.Combine(staging, "job")).FullName;
            var monsterDir = Directory.CreateDirectory(Path.Combine(staging, "monster")).FullName;

            // 封包读取串行（FF16Pack 不保证线程安全），解码与写 PNG 并行
            var sources = new List<(string Path, byte[] Texture, byte[]? Parts)>();
            lock (GameFileReader.Gate)
            {
                GameFileReader.EnsureDirectStorage(gameRoot);
                using var pack = FF16Pack.Open(PackPath(gameRoot), "ffto");
                foreach (var path in pack.Files.Keys.Where(path => TexturePath().IsMatch(path)).Order(StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    byte[]? parts = null;
                    if (path.StartsWith(FacePrefix, StringComparison.Ordinal))
                    {
                        var partsPath = PartsPath(path);
                        if (!pack.FileExists(partsPath))
                            throw new InvalidDataException($"Missing texture parts: {partsPath}");
                        parts = pack.GetFileDataBytes(partsPath);
                    }
                    sources.Add((path, pack.GetFileDataBytes(path), parts));
                }
            }
            if (sources.Count == 0)
                throw new InvalidDataException("No portrait textures found in the pack.");

            int faces = 0, jobs = 0, monsters = 0, skipped = 0;
            var written = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
            Parallel.ForEach(sources, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, source =>
            {
                var stem = Path.GetFileNameWithoutExtension(source.Path);
                if (!TryDecode(source.Texture, out var rgba, out var width, out var height))
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }
                if (source.Parts == null)
                {
                    File.WriteAllBytes(Path.Combine(jobDir, stem + ".png"), PngWriter.Encode(rgba, width, height));
                    written[$"job/{stem}.png"] = 0;
                    Interlocked.Increment(ref jobs);
                    return;
                }
                var all = ReadTextureParts(source.Parts).Where(part => part.Name == "All").ToArray();
                if (all.Length != 1)
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }
                var crop = Crop(rgba, width, height, all[0]);
                var png = PngWriter.Encode(crop, all[0].Right - all[0].X, all[0].Bottom - all[0].Y);
                File.WriteAllBytes(Path.Combine(faceDir, stem + ".png"), png);
                written[$"face/{stem}.png"] = 0;
                Interlocked.Increment(ref faces);
                // 怪物也在 common/face：134..149 为 16 个种族，08..10 为各三种职业
                var match = MonsterFace().Match(stem);
                // 154 是劳动八号（铁巨人）的机甲头像，也按怪物处理
                if (match.Success && int.Parse(match.Groups[1].Value) is >= 134 and <= 149 or 154)
                {
                    File.WriteAllBytes(Path.Combine(monsterDir, stem + ".png"), png);
                    written[$"monster/{stem}.png"] = 0;
                    Interlocked.Increment(ref monsters);
                }
            });

            var faceIds = written.Keys.Select(path => FaceId().Match(path)).Where(match => match.Success)
                .Select(match => int.Parse(match.Groups[1].Value)).ToHashSet();
            var specialFaces = BuildSpecialFaces(ReadCharacterNames(gameRoot), faceIds);
            var mappings = BuildMappings();
            foreach (var mapping in mappings)
                foreach (var relative in new[] { mapping.Face, mapping.JobIllustration })
                    if (relative != null && !written.ContainsKey(relative))
                        throw new InvalidDataException($"Mapped portrait is missing: {relative}");
            WriteManifest(Path.Combine(staging, "portraits.json"), fingerprint, mappings, specialFaces);
            if (Fingerprint(gameRoot) != fingerprint)
                throw new IOException("The portrait pack changed during extraction.");

            Replace(staging, output);
            return new PortraitExtractionResult(faces, jobs, monsters, mappings.Count, skipped, started.Elapsed);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    internal static string PartsPath(string texturePath) =>
        Path.ChangeExtension(texturePath.Replace("/texture/", "/textureparts/"), ".utexpt").Replace('\\', '/');

    // 与 ps1 的 Get-TextureParts 一致：+4 记录起点、+8 记录数；每条 20 字节 = 名称相对偏移 + x/y/right/bottom
    public static IReadOnlyList<TexturePart> ReadTextureParts(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12)
            throw new InvalidDataException("Invalid texture parts file.");
        var start = BitConverter.ToUInt32(bytes[4..]);
        var count = BitConverter.ToUInt32(bytes[8..]);
        if (start + 20L * count > bytes.Length)
            throw new InvalidDataException("Texture part records are out of range.");
        var parts = new List<TexturePart>((int)count);
        for (var i = 0; i < count; i++)
        {
            var offset = (int)(start + 20 * i);
            var nameStart = offset + (long)BitConverter.ToUInt32(bytes[offset..]);
            if (nameStart >= bytes.Length)
                throw new InvalidDataException("Texture part name is out of range.");
            var nameLength = bytes[(int)nameStart..].IndexOf((byte)0);
            if (nameLength < 0)
                throw new InvalidDataException("Texture part name is out of range.");
            parts.Add(new TexturePart(System.Text.Encoding.UTF8.GetString(bytes.Slice((int)nameStart, nameLength)),
                BitConverter.ToInt32(bytes[(offset + 4)..]), BitConverter.ToInt32(bytes[(offset + 8)..]),
                BitConverter.ToInt32(bytes[(offset + 12)..]), BitConverter.ToInt32(bytes[(offset + 16)..])));
        }
        return parts;
    }

    internal static byte[] Crop(byte[] rgba, int width, int height, TexturePart rect)
    {
        var w = rect.Right - rect.X;
        var h = rect.Bottom - rect.Y;
        if (rect.X < 0 || rect.Y < 0 || w <= 0 || h <= 0 || rect.Right > width || rect.Bottom > height)
            throw new InvalidDataException($"Crop rectangle {rect} is outside {width}x{height}.");
        var result = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            Buffer.BlockCopy(rgba, ((rect.Y + y) * width + rect.X) * 4, result, y * w * 4, w * 4);
        return result;
    }

    // 解第 0 个纹理的第 0 级 mip，输出 RGBA8。分块纹理每行按 256 字节对齐（同 TextureFile.GetAsDds）。
    internal static bool TryDecode(byte[] file, out byte[] rgba, out int width, out int height)
    {
        rgba = [];
        width = height = 0;
        var texture = new TextureFile();
        using var stream = new MemoryStream(file, writable: false);
        texture.FromStream(stream);
        if (texture.Textures.Count == 0)
            return false;
        var info = texture.Textures[0];
        if (info.DimensionType is 2 or 3)
            return false;
        width = info.Width;
        height = info.Height;
        var format = TextureUtils.TexPixelFormatToDxgiFormat(info.PixelFormat);
        stream.Position = 0;
        using var data = texture.GetTextureData(0, stream);
        DxgiUtils.ComputePitch(format, info.Width, info.Height, out var rowPitch, out var slicePitch, out _);
        var alignedPitch = info.ChunkCount == 0 ? (uint)rowPitch
            : TextureUtils.Align((uint)rowPitch, DxgiUtils.D3D12_TEXTURE_DATA_PITCH_ALIGNMENT);
        var rows = DxgiUtils.IsBCnFormat(format) ? (info.Height + 3) / 4 : info.Height;
        var packed = new byte[slicePitch];
        for (var y = 0; y < rows; y++)
        {
            var from = (int)(y * alignedPitch);
            if (from + (int)rowPitch > data.Length)
                return false;
            data.Span.Slice(from, (int)rowPitch).CopyTo(packed.AsSpan((int)(y * (uint)rowPitch)));
        }
        switch (format)
        {
            case DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM or DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
                rgba = packed;
                return rgba.Length == width * height * 4;
            case DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
                rgba = packed;
                if (rgba.Length != width * height * 4)
                    return false;
                for (var i = 0; i < rgba.Length; i += 4)
                    (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
                return true;
        }
        CompressionFormat? bc = format switch
        {
            DXGI_FORMAT.DXGI_FORMAT_BC7_UNORM or DXGI_FORMAT.DXGI_FORMAT_BC7_UNORM_SRGB or DXGI_FORMAT.DXGI_FORMAT_BC7_TYPELESS => CompressionFormat.Bc7,
            DXGI_FORMAT.DXGI_FORMAT_BC1_UNORM or DXGI_FORMAT.DXGI_FORMAT_BC1_UNORM_SRGB => CompressionFormat.Bc1WithAlpha,
            DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM or DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM_SRGB => CompressionFormat.Bc3,
            DXGI_FORMAT.DXGI_FORMAT_BC4_UNORM => CompressionFormat.Bc4,
            DXGI_FORMAT.DXGI_FORMAT_BC5_UNORM => CompressionFormat.Bc5,
            _ => null
        };
        if (bc == null)
            return false;
        var pixels = new BcDecoder().DecodeRaw(packed, width, height, bc.Value);
        rgba = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length && i < width * height; i++)
        {
            rgba[i * 4] = pixels[i].r;
            rgba[i * 4 + 1] = pixels[i].g;
            rgba[i * 4 + 2] = pixels[i].b;
            rgba[i * 4 + 3] = pixels[i].a;
        }
        return true;
    }

    // ID 来自名册的 PSX 职业顺序；与 extract-portraits.ps1 的映射规则逐条一致
    public static IReadOnlyList<PortraitMapping> BuildMappings()
    {
        var mappings = new List<PortraitMapping> { new("0x01", "0x01", "male", "face/wldface_001_08_uitx.png", null) };
        for (var index = 0; index < 20; index++)
            foreach (var female in new[] { false, true })
            {
                // 吟游诗人（17）只有男、舞者（18）只有女
                if (index == 17 && female || index == 18 && !female)
                    continue;
                var sprite = index < 17 ? 96 + 2 * index + (female ? 1 : 0)
                    : index == 17 ? 130 : index == 18 ? 131 : 132 + (female ? 1 : 0);
                mappings.Add(new(female ? "0x81" : "0x80", $"0x{0x4A + index:X2}", female ? "female" : "male",
                    $"face/wldface_{sprite:000}_08_uitx.png", $"job/jv_{index + 1:00}_{(female ? 'f' : 'm')}_uitx.png"));
            }
        // Job-en 表 0x5E..0x8D 每三个职业共用一个种族头像；0xA9..0xAD 是同 jobtype 的剧情别名
        var aliases = new Dictionary<int, int> { [0xA9] = 0x67, [0xAA] = 0x61, [0xAB] = 0x62, [0xAC] = 0x64, [0xAD] = 0x5E };
        foreach (var jobId in Enumerable.Range(0x5E, 0x8D - 0x5E + 1).Concat(Enumerable.Range(0xA9, 5)))
        {
            var index = aliases.GetValueOrDefault(jobId, jobId) - 0x5E;
            mappings.Add(new("0x82", $"0x{jobId:X2}", "monster",
                $"monster/wldface_{134 + index / 3:000}_{8 + index % 3:00}_uitx.png", null));
        }
        // 劳动八号（铁巨人 0x91）不在种族头像区间，头像是 154 号机甲（2026-11-16 实机名册 + 目视核对）
        // PortraitCatalog 对 monster 只认 monster/ 目录，所以提取时 154 也复制到 monster/
        mappings.Add(new("0x82", "0x91", "monster", "monster/wldface_154_08_uitx.png", null));
        return mappings;
    }

    private static void WriteManifest(string path, string fingerprint, IReadOnlyList<PortraitMapping> mappings,
        IReadOnlyDictionary<int, int> specialFaces)
    {
        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteNumber("schemaVersion", 1);
        json.WriteString("generator", "FftTrainer runtime extractor");
        json.WriteNumber("extractorVersion", SchemaVersion);
        json.WriteString("sourcePack", PackRelativePath);
        json.WriteString("sourceFingerprint", fingerprint);
        json.WriteStartArray("rosterMappings");
        foreach (var mapping in mappings)
        {
            json.WriteStartObject();
            json.WriteString("characterCode", mapping.CharacterCode);
            json.WriteString("jobId", mapping.JobId);
            json.WriteString("gender", mapping.Gender);
            json.WriteString("face", mapping.Face);
            if (mapping.JobIllustration == null)
                json.WriteNull("jobIllustration");
            else
                json.WriteString("jobIllustration", mapping.JobIllustration);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("specialFaces");
        foreach (var (code, face) in specialFaces)
        {
            json.WriteStartObject();
            json.WriteString("characterCode", $"0x{code:X2}");
            json.WriteString("face", $"face/wldface_{face:000}_08_uitx.png");
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    // 先把旧缓存挪开，再把临时目录挪到位；失败时尽量恢复旧缓存
    private static void Replace(string staging, string output)
    {
        string? backup = null;
        if (Directory.Exists(output))
        {
            backup = output.TrimEnd(Path.DirectorySeparatorChar) + $".old-{Guid.NewGuid():N}";
            Directory.Move(output, backup);
        }
        try
        {
            Directory.Move(staging, output);
        }
        catch
        {
            if (backup != null && !Directory.Exists(output))
                Directory.Move(backup, output);
            throw;
        }
        if (backup != null)
            TryDelete(backup);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 留下的临时目录不影响功能，下次运行再清
        }
    }
}
