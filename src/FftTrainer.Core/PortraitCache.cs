using FftTrainer.GameFiles;

namespace FftTrainer.Core;

// 立绘缓存：游戏目录确定后在后台从玩家自己的游戏封包提取立绘（PortraitExtractor），
// 写到 %LOCALAPPDATA%\FftTrainer\portraits；PortraitCatalog 本来就读这个目录。
// 封包没变就不重做；任何失败只记日志，不影响修改器其他功能。
public sealed class PortraitCache
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly Func<string, string, bool> isUpToDate;
    private readonly Func<string, string, PortraitExtractionResult> extract;
    private readonly Action<string>? log;
    private string? requestedRoot;
    private Task queue = Task.CompletedTask;

    public PortraitCache(string directory, Action<string>? log = null,
        Func<string, string, bool>? isUpToDate = null, Func<string, string, PortraitExtractionResult>? extract = null)
    {
        this.directory = directory;
        this.log = log;
        this.isUpToDate = isUpToDate ?? PortraitExtractor.IsUpToDate;
        this.extract = extract ?? ((root, output) => PortraitExtractor.Extract(root, output));
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FftTrainer", "portraits");

    // 提取完成并换上新缓存后触发（在后台线程上）
    public event Action? Updated;

    // 同一个游戏目录只排一次；返回排队的任务，测试用
    public Task Ensure(string? gameRoot)
    {
        lock (gate)
        {
            if (gameRoot == null || string.Equals(gameRoot, requestedRoot, StringComparison.OrdinalIgnoreCase))
                return queue;
            requestedRoot = gameRoot;
            return queue = queue.ContinueWith(_ => Run(gameRoot), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void Run(string gameRoot)
    {
        try
        {
            if (isUpToDate(gameRoot, directory))
                return;
            var result = extract(gameRoot, directory);
            log?.Invoke($"portraits extracted: faces={result.Faces} jobs={result.Jobs} monsters={result.Monsters} "
                + $"mappings={result.Mappings} skipped={result.Skipped} in {result.Elapsed.TotalSeconds:F1}s");
            Updated?.Invoke();
        }
        catch (Exception ex)
        {
            log?.Invoke($"portrait extraction failed: {ex}");
        }
    }
}
