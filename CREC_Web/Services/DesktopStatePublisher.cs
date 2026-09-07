using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CREC_Web.Services;

/// <summary>Private, same-user IPC: project paths are never exposed through the public status API.</summary>
public sealed class DesktopStatePublisher : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamWriter _writer;

    private DesktopStatePublisher(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    }

    public static async Task<DesktopStatePublisher> ConnectAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(5000); return new(pipe); }
        catch { await pipe.DisposeAsync(); throw; }
    }

    public async Task RunAsync(ProjectRuntime runtime, CancellationToken stop)
    {
        ProjectState? previous = null;
        try
        {
            while (true)
            {
                var state = runtime.Current;
                if (state != previous)
                {
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(state));
                    previous = state;
                }
                await Task.Delay(200, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The caller stops this loop AFTER Kestrel has drained shutdown requests.
            await _writer.WriteLineAsync(JsonSerializer.Serialize(runtime.Current));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        await _pipe.DisposeAsync();
    }
}
