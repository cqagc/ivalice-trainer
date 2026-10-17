using System.Runtime.InteropServices;
using FF16Tools.Files.Nex;
using FF16Tools.Pack;

namespace FftTrainer.GameFiles;

// 一行 Nex 表：Key 加上按 layout 列名索引的单元格
public sealed record GameTableRow(int Key, IReadOnlyDictionary<string, object?> Cells)
{
    public string? Text(string column) => Cells.GetValueOrDefault(column) as string;

    public int? Number(string column) => Cells.GetValueOrDefault(column) switch
    {
        int value => value,
        uint value when value <= int.MaxValue => (int)value,
        short value => value,
        ushort value => value,
        byte value => value,
        sbyte value => value,
        _ => null,
    };
}

// 从玩家本机的游戏目录读本地化 Nex 表（data/enhanced/0004.<语言>.pac 里的 nxd/<表>.<语言>.nxd）。
// 0004 封包里的 nxd 用 GDeflate 压缩，解压要 DirectStorage 的原生库：直接借用游戏目录自带的
// dstoragecore.dll / dstorage.dll（先按完整路径加载，之后按名字解析 dstorage.dll 时复用已加载的模块）。
public static class GameFileReader
{
    public const string ExecutableName = "FFT_enhanced.exe";
    private static readonly object Gate = new();
    private static bool directStorageLoaded;

    public static string EnhancedDirectory(string gameRoot) => Path.Combine(gameRoot, "data", "enhanced");

    public static IReadOnlyDictionary<string, IReadOnlyList<GameTableRow>> ReadLocalizedTables(
        string gameRoot, string language, IEnumerable<string> tables)
    {
        lock (Gate)
        {
            EnsureDirectStorage(gameRoot);
            var result = new Dictionary<string, IReadOnlyList<GameTableRow>>();
            using var pack = FF16Pack.Open(Path.Combine(EnhancedDirectory(gameRoot), $"0004.{language}.pac"), "ffto");
            foreach (var table in tables)
                result[table] = ReadTable(pack, table, $"nxd/{table}.{language}.nxd");
            return result;
        }
    }

    private static List<GameTableRow> ReadTable(FF16Pack pack, string table, string path)
    {
        var nex = new NexDataFile();
        nex.Read(pack.GetFileDataBytes(path));
        var layout = TableMappingReader.ReadTableLayout(table, new Version(1, 0, 0), "ffto");
        var columns = layout.Columns.Keys.ToArray();
        var rows = new List<GameTableRow>();
        foreach (var row in nex.RowManager!.GetAllRowInfos())
        {
            var cells = NexUtils.ReadRow(layout, nex.Buffer!, row.RowDataOffset);
            var map = new Dictionary<string, object?>(columns.Length);
            for (var i = 0; i < columns.Length && i < cells.Count; i++)
                map[columns[i]] = cells[i];
            rows.Add(new GameTableRow((int)row.Key, map));
        }
        return rows;
    }

    private static void EnsureDirectStorage(string gameRoot)
    {
        if (directStorageLoaded)
            return;
        NativeLibrary.Load(Path.Combine(gameRoot, "dstoragecore.dll"));
        NativeLibrary.Load(Path.Combine(gameRoot, "dstorage.dll"));
        directStorageLoaded = true;
    }
}
