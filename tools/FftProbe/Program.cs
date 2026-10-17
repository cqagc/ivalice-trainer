using System.Globalization;
using System.Text;
using FftTrainer.Core;

Console.OutputEncoding = Encoding.ASCII;
Console.InputEncoding = Encoding.UTF8;
try
{
    var arguments = args.ToList();
    var processName = "FFT_enhanced";
    for (var i = 0; i < arguments.Count;)
    {
        if (arguments[i] != "--process")
        {
            i++;
            continue;
        }
        if (i + 1 == arguments.Count)
            throw new ArgumentException("--process requires a name.");
        processName = arguments[i + 1];
        arguments.RemoveRange(i, 2);
    }
    if (arguments.Count == 0)
        throw new ArgumentException("Commands: info, scan, next, list, read, write, aob, find-seq, find-str, gametext, gametext-locate.");
    var command = arguments[0];
    var parameters = arguments.Skip(1).ToArray();
    if (command == "gametext")
        return GameTextCommand(parameters);
    if (command == "gametext-locate")
        return GameTextLocateCommand();
    using var process = new GameProcess(processName);

    void Require(int minimum, int maximum)
    {
        if (parameters.Length < minimum || parameters.Length > maximum)
            throw new ArgumentException($"Invalid arguments for '{command}'.");
    }

    ulong Address(string text)
    {
        var relative = text.StartsWith("base+", StringComparison.OrdinalIgnoreCase);
        if (relative)
            text = text[5..];
        var value = Values.Parse(text, ValueTypeCode.U64);
        return checked(value + (relative ? process.ModuleBase : 0));
    }

    ulong ParseLength(string text) => Values.Parse(text, ValueTypeCode.U64);

    string Session(string name)
    {
        if (name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("Session names allow ASCII letters, digits, '-' and '_'.");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FftProbe", "sessions");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name + ".bin");
    }

    void PrintMatches(IEnumerable<ulong> matches)
    {
        long count = 0;
        foreach (var address in matches)
        {
            var offset = address >= process.ModuleBase
                ? $"+0x{address - process.ModuleBase:X}" : $"-0x{process.ModuleBase - address:X}";
            Console.WriteLine($"0x{address:X} module{offset}");
            count++;
        }
        Console.WriteLine($"Matches: {count}");
    }

    switch (command)
    {
        case "info":
        {
            Require(0, 0);
            var regions = process.GetRegions().ToArray();
            Console.WriteLine($"PID: {process.ProcessId}");
            Console.WriteLine($"Module: {process.ModulePath}");
            Console.WriteLine($"Base: 0x{process.ModuleBase:X}");
            Console.WriteLine($"Size: 0x{process.ModuleSize:X}");
            Console.WriteLine($"Writable regions: {regions.Length}");
            Console.WriteLine($"Writable bytes: {regions.Aggregate(0UL, (sum, r) => sum + r.Size)}");
            break;
        }
        case "scan":
        {
            Require(3, 3);
            var type = Values.ParseType(parameters[1]);
            var value = Values.Parse(parameters[2], type);
            var count = new ValueScanner(process).Scan(Session(parameters[0]), type, value);
            Console.WriteLine($"Matches: {count}");
            break;
        }
        case "next":
        {
            Require(2, 3);
            var path = Session(parameters[0]);
            ValueTypeCode type;
            using (var reader = new BinaryReader(File.OpenRead(path)))
                type = ResultFile.ReadHeader(reader);
            var op = parameters[1];
            Values.ValidateOperation(op);
            var needsValue = op is "eq" or "ne" or "gt" or "lt";
            if (parameters.Length != (needsValue ? 3 : 2))
                throw new ArgumentException("eq/ne/gt/lt require a value; other operations take no value.");
            var target = needsValue ? Values.Parse(parameters[2], type) : 0;
            Console.WriteLine($"Matches: {new ValueScanner(process).Next(path, op, target)}");
            break;
        }
        case "list":
        {
            Require(1, 2);
            var max = parameters.Length == 2 ? int.Parse(parameters[1], CultureInfo.InvariantCulture) : 50;
            ArgumentOutOfRangeException.ThrowIfNegative(max);
            using var reader = new BinaryReader(File.OpenRead(Session(parameters[0])));
            var type = ResultFile.ReadHeader(reader);
            foreach (var result in ResultFile.Read(reader).Take(max))
            {
                string current;
                try
                {
                    current = Values.Format(Values.Bits(process.ReadBytes(result.Address, Values.Size(type)), type), type);
                }
                catch (IOException)
                {
                    current = "<unreadable>";
                }
                Console.WriteLine($"0x{result.Address:X} current={current} previous={Values.Format(result.Previous, type)}");
            }
            break;
        }
        case "read":
        {
            Require(1, 2);
            var address = Address(parameters[0]);
            var length = parameters.Length == 2 ? int.Parse(parameters[1], CultureInfo.InvariantCulture) : 256;
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            for (var offset = 0; offset < length;)
            {
                var bytes = process.ReadBytes(checked(address + (ulong)offset), Math.Min(16, length - offset));
                var hex = string.Join(" ", bytes.Select(b => b.ToString("X2")));
                var ascii = new string(bytes.Select(b => b is >= 32 and <= 126 ? (char)b : '.').ToArray());
                Console.WriteLine($"0x{address + (ulong)offset:X16}  {hex,-47}  |{ascii}|");
                offset += bytes.Length;
            }
            break;
        }
        case "dump":
        {
            // dump <addr> <len> <file>：原样导出内存，读不了的页填 0
            Require(3, 3);
            var address = Address(parameters[0]);
            var length = ParseLength(parameters[1]);
            using var output = File.Create(parameters[2]);
            for (ulong offset = 0; offset < length;)
            {
                var size = (int)Math.Min(0x10000UL, length - offset);
                byte[] bytes;
                try { bytes = process.ReadBytes(address + offset, size); }
                catch (IOException) { bytes = new byte[size]; }
                output.Write(bytes);
                offset += (ulong)size;
            }
            Console.WriteLine($"Dumped {length} bytes from 0x{address:X} to {parameters[2]}");
            break;
        }
        case "write":
        {
            Require(3, 3);
            var address = Address(parameters[0]);
            var type = Values.ParseType(parameters[1]);
            process.WriteBytes(address, Values.ToBytes(Values.Parse(parameters[2], type), type));
            Console.WriteLine($"Wrote {Values.Size(type)} bytes at 0x{address:X}.");
            break;
        }
        case "aob":
        {
            Require(1, 2);
            if (parameters.Length == 2 && parameters[1] != "--all-memory")
                throw new ArgumentException("Expected --all-memory.");
            var regions = parameters.Length == 2 ? process.GetRegions()
                : new[] { new MemoryRegion(process.ModuleBase, process.ModuleSize) };
            PrintMatches(AobScanner.Scan(process, new AobPattern(parameters[0]), regions));
            break;
        }
        case "find-seq":
        {
            Require(1, int.MaxValue);
            PrintMatches(SequenceScanner.Scan(process, parameters.Select(SequenceField.Parse).ToArray()));
            break;
        }
        case "find-str":
        {
            Require(1, 2);
            if (parameters.Length == 2 && parameters[1] != "--utf16")
                throw new ArgumentException("Expected --utf16.");
            var encoding = parameters.Length == 2 ? Encoding.Unicode : Encoding.UTF8;
            var pattern = string.Join(" ", encoding.GetBytes(parameters[0]).Select(b => b.ToString("X2")));
            PrintMatches(AobScanner.Scan(process, new AobPattern(pattern), process.GetRegions()));
            break;
        }
        default:
            throw new ArgumentException($"Unknown command '{command}'.");
    }
    return 0;
}
catch (Exception exception)
{
    // Keep diagnostics ASCII even when Windows returns localized messages.
    Console.Error.WriteLine($"ERROR: {exception.Message}");
    return 1;
}

// gametext <cs|ct|en|fr|de|ja|ko> [table] [key] [--game <游戏根目录>]
// 不连游戏进程，只读游戏目录里的封包；输出 UTF-8（Windows 控制台先设 [Console]::OutputEncoding = UTF8）
static int GameTextCommand(string[] parameters)
{
    var list = parameters.ToList();
    // 默认按 Steam 库自动找游戏目录，也可以用 --game 指定
    var root = GameTextService.FindSteamInstall();
    var gameIndex = list.IndexOf("--game");
    if (gameIndex >= 0)
    {
        if (gameIndex + 1 >= list.Count)
            throw new ArgumentException("--game requires a directory.");
        root = list[gameIndex + 1];
        list.RemoveRange(gameIndex, 2);
    }
    if (list.Count is < 1 or > 3)
        throw new ArgumentException("Usage: gametext <lang> [table] [key] [--game <dir>]");
    if (root == null)
        throw new ArgumentException("Game directory not found via Steam; pass --game <dir>.");
    Console.OutputEncoding = Encoding.UTF8;
    var language = list[0];
    var tables = list.Count >= 2 ? new[] { list[1] } : GameTextData.Tables.ToArray();
    int? key = list.Count == 3 ? int.Parse(list[2], CultureInfo.InvariantCulture) : null;
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var result = FftTrainer.GameFiles.GameFileReader.ReadLocalizedTables(root, language, tables);
    var elapsed = watch.ElapsedMilliseconds;
    foreach (var (table, rows) in result)
    {
        Console.WriteLine($"[{table}] {rows.Count} rows");
        foreach (var row in rows.Where(row => key == null || row.Key == key))
        {
            if (key == null && list.Count < 2)
                break;
            var texts = row.Cells.Where(cell => cell.Value is string { Length: > 0 }).Select(cell => $"{cell.Key}={cell.Value}");
            var numbers = row.Cells.Where(cell => cell.Value is int or uint).Select(cell => $"{cell.Key}={cell.Value}");
            Console.WriteLine($"{row.Key}: {string.Join(" | ", texts.Concat(numbers))}");
        }
    }
    Console.WriteLine($"Read {result.Count} table(s) in {elapsed} ms");
    return 0;
}

// gametext-locate：走修改器的真实路径（Steam 库定位 + 缓存），打印每种语言的几个名称
static int GameTextLocateCommand()
{
    Console.OutputEncoding = Encoding.UTF8;
    var root = GameTextService.FindSteamInstall();
    Console.WriteLine($"Steam install: {root ?? "<not found>"}");
    var catalog = new GameTextCatalog();
    var service = new GameTextService(catalog, GameTextService.DefaultCacheDirectory);
    service.SetGameRoot(root);
    foreach (var language in UiLanguages.All)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        service.Load(language);
        Console.WriteLine($"{language.Code,-8} {watch.ElapsedMilliseconds,4} ms  {catalog.JobName(0x4A)} / {catalog.ItemName(0xF0)} / "
            + $"{catalog.UnitName(1)} / {catalog.SkillGroupName(5)} / {catalog.PoachingName(95)} / {catalog.ItemCategory(0x13)}");
    }
    return 0;
}
