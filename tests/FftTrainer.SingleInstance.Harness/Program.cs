using System.Diagnostics;
using System.Reflection;
using FftTrainer.App;

// No WPF, Core, GameSession, process-memory APIs or production mutex identity.
// Can run in a non-interactive Windows SSH session without touching the game.
internal static class Program
{
    private static readonly List<Process> Children = [];
    private static readonly string AssemblyPath = Assembly.GetExecutingAssembly().Location;

    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This harness requires Windows.");
            return 1;
        }
        if (args.Length > 0 && args[0] == "--worker")
            return Worker(args[1], args[2]); // Synchronous: acquire and release on this thread.

        string? copyDirectory = null;
        try
        {
            var identity = NewIdentity();
            var owner = Start(identity, "delayed");
            await Expect(owner, "PRIMARY");
            await ExpectSecondary(Start(identity));
            await Expect(owner, "ACTIVATED");
            Console.WriteLine("PASS: duplicate waits for early-starting owner and receives activation ACK");

            for (var i = 0; i < 3; i++)
            {
                await ExpectSecondary(Start(identity));
                await Expect(owner, "ACTIVATED");
            }
            Console.WriteLine("PASS: repeated activation requests");

            copyDirectory = Path.Combine(Path.GetTempPath(), "FftTrainer.SingleInstance.Harness-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(copyDirectory);
            foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
                File.Copy(file, Path.Combine(copyDirectory, Path.GetFileName(file)));
            await ExpectSecondary(Start(identity, assemblyPath: Path.Combine(copyDirectory, Path.GetFileName(AssemblyPath))));
            await Expect(owner, "ACTIVATED");
            Console.WriteLine("PASS: executable copy in another directory shares ownership and IPC");

            await Stop(owner);
            var replacement = Start(identity);
            await Expect(replacement, "PRIMARY");
            await Stop(replacement);
            Console.WriteLine("PASS: normal exit releases ownership");

            identity = NewIdentity();
            owner = Start(identity, "silent");
            await Expect(owner, "PRIMARY");
            var contender = Start(identity);
            await Task.Delay(300);
            await Stop(owner);
            await Expect(contender, "PRIMARY");
            await Stop(contender);
            Console.WriteLine("PASS: exiting owner without IPC is replaced during activation retries");

            identity = NewIdentity();
            owner = Start(identity, "silent");
            await Expect(owner, "PRIMARY");
            contender = Start(identity);
            await Task.Delay(300);
            owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Expect(contender, "PRIMARY");
            await Stop(contender);
            Console.WriteLine("PASS: crashed owner is replaced (including abandoned mutex recovery)");

            identity = NewIdentity();
            var racers = Enumerable.Range(0, 8).Select(_ => Start(identity)).ToArray();
            var results = await Task.WhenAll(racers.Select(ReadLine));
            Check(results.Count(result => result == "PRIMARY") == 1, "Expected exactly one primary among eight processes.");
            for (var i = 0; i < racers.Length; i++)
            {
                if (results[i] == "PRIMARY")
                    await Stop(racers[i]);
                else
                {
                    Check(results[i] == "SECONDARY", "Unexpected contention result: " + results[i]);
                    await ExpectExit(racers[i], 0);
                }
            }
            Console.WriteLine("PASS: eight concurrent processes elect exactly one owner");

            identity = NewIdentity();
            owner = Start(identity, "pending");
            await Expect(owner, "PRIMARY");
            contender = Start(identity);
            await Expect(owner, "ACTIVATION_PENDING");
            await Stop(owner);
            await Expect(contender, "PRIMARY");
            await Stop(contender);
            Console.WriteLine("PASS: shutdown cancels pending activation without deadlocking owner thread");

            identity = NewIdentity();
            owner = Start(identity, "silent");
            await Expect(owner, "PRIMARY");
            contender = Start(identity);
            await Expect(contender, "BLOCKED");
            await ExpectExit(contender, 2);
            Check(!owner.HasExited, "Unresponsive owner unexpectedly exited.");
            await Stop(owner);
            Console.WriteLine("PASS: unresponsive owner fails closed without a second primary");
            Console.WriteLine("All single-instance checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            foreach (var child in Children)
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit(5000);
                }
                child.Dispose();
            }
            if (copyDirectory is not null)
                Directory.Delete(copyDirectory, recursive: true);
        }
    }

    private static int Worker(string identity, string mode)
    {
        try
        {
            using (var instance = new SingleInstance(identity))
            {
                if (!instance.EnterOrActivate())
                {
                    Console.WriteLine("SECONDARY");
                    return 0;
                }
                Console.WriteLine("PRIMARY");
                if (mode == "delayed")
                    Thread.Sleep(1500);
                if (mode != "silent")
                {
                    instance.StartListening(token =>
                    {
                        if (mode == "pending")
                        {
                            Console.WriteLine("ACTIVATION_PENDING");
                            // Deliberately never complete, like a dispatcher stopped in OnExit.
                            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
                        }
                        Console.WriteLine("ACTIVATED");
                        return Task.FromResult(true);
                    }, ex => Console.Error.WriteLine(ex));
                }
                Console.ReadLine();
            }
            return 0;
        }
        catch (TimeoutException)
        {
            Console.WriteLine("BLOCKED");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static string NewIdentity() => "FftTrainer.SingleInstance.Test." + Guid.NewGuid().ToString("N");

    private static Process Start(string identity, string mode = "normal", string? assemblyPath = null)
    {
        var current = Environment.ProcessPath!;
        var host = Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? current : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(assemblyPath ?? AssemblyPath)!
        };
        start.ArgumentList.Add(assemblyPath ?? AssemblyPath);
        start.ArgumentList.Add("--worker");
        start.ArgumentList.Add(identity);
        start.ArgumentList.Add(mode);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start worker.");
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
        process.BeginErrorReadLine();
        Children.Add(process);
        return process;
    }

    private static async Task<string?> ReadLine(Process process) =>
        await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(25));

    private static async Task Expect(Process process, string expected)
    {
        var actual = await ReadLine(process);
        Check(actual == expected, $"Worker {process.Id}: expected {expected}, got {actual ?? "EOF"}.");
    }

    private static async Task ExpectSecondary(Process process)
    {
        await Expect(process, "SECONDARY");
        await ExpectExit(process, 0);
    }

    private static async Task Stop(Process process)
    {
        await process.StandardInput.WriteLineAsync("exit");
        await process.StandardInput.FlushAsync();
        await ExpectExit(process, 0);
    }

    private static async Task ExpectExit(Process process, int expected)
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(process.ExitCode == expected, $"Worker {process.Id}: expected exit {expected}, got {process.ExitCode}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
