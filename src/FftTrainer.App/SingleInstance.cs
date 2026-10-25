using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace FftTrainer.App;

/// <summary>
/// One owner per Windows session, independent of executable name or directory.
/// Construct, enter and dispose on the same thread: Windows mutex ownership is thread-affine.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string ProductIdentity = "FftTrainer.6D3D969B-6304-451A-8BD7-B0640A351456";
    private const byte ActivateRequest = 1;
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    private bool _ownsMutex;

    internal SingleInstance(string identity = ProductIdentity)
    {
        _mutex = new Mutex(false, @"Local\" + identity);
        _pipeName = identity + "." + Process.GetCurrentProcess().SessionId;
    }

    /// <returns>True only when this thread owns the mutex; false after an acknowledged activation.</returns>
    internal bool EnterOrActivate()
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            try
            {
                _ownsMutex = _mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // Windows transferred ownership after the previous owner crashed.
                _ownsMutex = true;
            }

            if (_ownsMutex)
                return true;

            // The owner may not have opened its pipe yet, or may be shutting down.
            // Retry BOTH the mutex and IPC rather than treating a failed connection as ownership.
            if (TryActivateAsync().GetAwaiter().GetResult())
                return false;
            Thread.Sleep(50);
        }

        // Fail closed: a hung/inaccessible owner must never result in a second writer.
        throw new TimeoutException("The existing FftTrainer instance did not respond.");
    }

    internal void StartListening(Func<CancellationToken, Task<bool>> activate, Action<Exception> log)
    {
        if (!_ownsMutex || _listener is not null)
            throw new InvalidOperationException("Only the mutex owner can start the activation listener.");

        // Create the first server synchronously so startup errors cannot silently disable IPC.
        var first = CreatePipe();
        _listener = Task.Run(async () =>
        {
            NamedPipeServerStream? server = first;
            try
            {
                while (!_stop.IsCancellationRequested && server is not null)
                {
                    using (server)
                    {
                        await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                        using var request = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        request.CancelAfter(TimeSpan.FromSeconds(2));
                        try
                        {
                            var buffer = new byte[1];
                            if (await server.ReadAsync(buffer, request.Token).ConfigureAwait(false) == 1 &&
                                buffer[0] == ActivateRequest &&
                                await activate(request.Token).WaitAsync(request.Token).ConfigureAwait(false))
                            {
                                // ACK only after the UI has accepted the request, not merely on connect.
                                await server.WriteAsync(buffer, request.Token).ConfigureAwait(false);
                            }
                        }
                        catch (IOException) { /* Client exited or retried. */ }
                        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                        catch (Exception ex) { log(ex); }
                    }
                    server = null;
                    if (!_stop.IsCancellationRequested)
                        server = CreatePipe();
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // Keep ownership on IPC failure: never allow another game writer.
                log(ex);
            }
            finally
            {
                server?.Dispose();
            }
        });
    }

    private NamedPipeServerStream CreatePipe() => new(_pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task<bool> TryActivateAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(new byte[] { ActivateRequest }, timeout.Token).ConfigureAwait(false);
            var reply = new byte[1];
            return await client.ReadAsync(reply, timeout.Token).ConfigureAwait(false) == 1 &&
                reply[0] == ActivateRequest;
        }
        catch (IOException) { return false; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        // The UI callback must observe this token instead of requiring the dispatcher to run.
        // Thus OnExit can safely join the listener on the mutex-owning UI thread.
        _stop.Cancel();
        try
        {
            _listener?.GetAwaiter().GetResult();
        }
        finally
        {
            if (_ownsMutex)
            {
                _mutex.ReleaseMutex();
                _ownsMutex = false;
            }
            _mutex.Dispose();
            _stop.Dispose();
        }
    }
}
