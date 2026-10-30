using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CodexKeeper.Windows.Services;

/// <summary>One app-server process and its newline-delimited JSON protocol connection.</summary>
public sealed class CodexAppServerClient : IAsyncDisposable
{
    private const int MaximumLineBytes = 8 * 1024 * 1024;
    private const int MaximumQueuedBytes = 32 * 1024 * 1024;
    private const int MaximumPendingBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private readonly Process _process;
    private readonly StreamWriter _input;
    private readonly Channel<(JsonObject Message, int ByteCount)> _messages = Channel.CreateBounded<(JsonObject, int)>(new BoundedChannelOptions(64)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true
    });
    private readonly Queue<(JsonObject Message, int ByteCount)> _pending = new();
    private readonly object _queueGate = new();
    private TaskCompletionSource _queueSpaceChanged = NewQueuePulse();
    private int _queuedBytes;
    private int _pendingBytes;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reader;
    private int _requestId;
    private bool _closed;

    private CodexAppServerClient(string executable, string codexHome, bool usageOnly)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Directory.Exists(codexHome) ? codexHome : Environment.CurrentDirectory
        };
        start.ArgumentList.Add("app-server");
        if (usageOnly)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("features.plugins=false");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("features.remote_plugin=false");
        }
        start.Environment["CODEX_HOME"] = codexHome;
        start.Environment["CODEX_INTERNAL_APP_SERVER_REMOTE_CONTROL_DISABLED"] = "1";
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!_process.Start()) throw new InvalidOperationException(Localization.Text("activity.noCli"));
        _input = _process.StandardInput;
        _input.AutoFlush = true;
        _ = _process.StandardError.ReadToEndAsync(); // Drain diagnostics without retaining account or prompt data.
        _reader = ReadMessagesAsync(_lifetime.Token);
    }

    public static async Task<CodexAppServerClient> ConnectAsync(string executable, string codexHome, bool usageOnly = false, CancellationToken cancellationToken = default)
    {
        var client = new CodexAppServerClient(executable, codexHome, usageOnly);
        try
        {
            await client.RequestAsync("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "codex_keeper_windows", ["title"] = "Codex Keeper", ["version"] = "1.0.1" },
                ["capabilities"] = new JsonObject { ["experimentalApi"] = true }
            }, cancellationToken);
            await client.NotifyAsync("initialized", null, cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<JsonObject> RequestAsync(string method, JsonObject? parameters, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        var id = Interlocked.Increment(ref _requestId);
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters?.DeepClone() ?? new JsonObject() };
        await WriteAsync(message, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(RequestTimeout);
        while (true)
        {
            var incoming = await ReadMessageAsync(timeout.Token);
            if (incoming.Message["id"]?.ToString() == id.ToString(System.Globalization.CultureInfo.InvariantCulture) && incoming.Message["method"] is null)
            {
                if (incoming.Message["error"] is JsonObject error)
                    throw new CodexRpcException(error["message"]?.ToString() ?? "Codex 请求失败");
                return incoming.Message["result"] as JsonObject ?? new JsonObject();
            }
            _pending.Enqueue(incoming);
            _pendingBytes += incoming.ByteCount;
            if (_pending.Count > 512 || _pendingBytes > MaximumPendingBytes)
                throw new InvalidDataException("Codex app-server sent too many unmatched messages.");
        }
    }

    public async Task NotifyAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default)
    {
        var message = new JsonObject { ["method"] = method };
        if (parameters is not null) message["params"] = parameters.DeepClone();
        await WriteAsync(message, cancellationToken);
    }

    public async Task<JsonObject> NextMessageAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_pending.Count > 0)
        {
            var pending = _pending.Dequeue();
            _pendingBytes -= pending.ByteCount;
            return pending.Message;
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        limit.CancelAfter(timeout);
        return (await ReadMessageAsync(limit.Token)).Message;
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var line = message.ToJsonString();
        await _input.WriteLineAsync(line.AsMemory(), cancellationToken);
        await _input.FlushAsync(cancellationToken);
    }

    private async Task<(JsonObject Message, int ByteCount)> ReadMessageAsync(CancellationToken cancellationToken)
    {
        if (await _messages.Reader.WaitToReadAsync(cancellationToken))
        {
            var message = await _messages.Reader.ReadAsync(cancellationToken);
            ReleaseQueuedBytes(message.ByteCount);
            return message;
        }
        throw new EndOfStreamException("Codex app-server closed its output stream.");
    }

    private async Task EnqueueMessageAsync(JsonObject message, int byteCount, CancellationToken cancellationToken)
    {
        if (byteCount > MaximumQueuedBytes) throw new InvalidDataException("Codex app-server response exceeded the queue limit.");
        while (true)
        {
            Task waitForSpace;
            lock (_queueGate)
            {
                if (_queuedBytes + byteCount <= MaximumQueuedBytes)
                {
                    _queuedBytes += byteCount;
                    waitForSpace = Task.CompletedTask;
                }
                else waitForSpace = _queueSpaceChanged.Task;
            }
            if (waitForSpace.IsCompleted) break;
            await waitForSpace.WaitAsync(cancellationToken);
        }
        try { await _messages.Writer.WriteAsync((message, byteCount), cancellationToken); }
        catch
        {
            ReleaseQueuedBytes(byteCount);
            throw;
        }
    }

    private void ReleaseQueuedBytes(int byteCount)
    {
        TaskCompletionSource changed;
        lock (_queueGate)
        {
            _queuedBytes -= byteCount;
            changed = _queueSpaceChanged;
            _queueSpaceChanged = NewQueuePulse();
        }
        changed.TrySetResult();
    }

    private static TaskCompletionSource NewQueuePulse() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task ReadMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stream = _process.StandardOutput.BaseStream;
            var chunk = new byte[8192];
            using var line = new MemoryStream();
            while (true)
            {
                var count = await stream.ReadAsync(chunk, cancellationToken);
                if (count == 0) break;
                for (var index = 0; index < count; index++)
                {
                    var value = chunk[index];
                    if (value == (byte)'\n')
                    {
                        if (line.Length == 0) continue;
                        var byteCount = checked((int)line.Length);
                        var bytes = line.GetBuffer().AsSpan(0, byteCount);
                        var message = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(bytes)
                            ?? throw new InvalidDataException("Codex app-server returned an empty JSON message.");
                        line.SetLength(0);
                        await EnqueueMessageAsync(message, byteCount, cancellationToken);
                    }
                    else
                    {
                        if (line.Length >= MaximumLineBytes) throw new InvalidDataException("Codex app-server response exceeded the read limit.");
                        line.WriteByte(value);
                    }
                }
            }
            if (line.Length > 0)
            {
                var message = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(line.GetBuffer().AsSpan(0, checked((int)line.Length)));
                if (message is not null) await EnqueueMessageAsync(message, checked((int)line.Length), cancellationToken);
            }
            _messages.Writer.TryComplete(new EndOfStreamException("Codex app-server exited."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _messages.Writer.TryComplete();
        }
        catch (Exception error)
        {
            _messages.Writer.TryComplete(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        try { _input.Close(); } catch { }
        try
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await _process.WaitForExitAsync(grace.Token);
        }
        catch
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        }
        try { await _reader; } catch { }
        _input.Dispose();
        _process.Dispose();
        _lifetime.Dispose();
    }
}

public sealed class CodexRpcException(string message) : Exception(message);
