using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AIContextSyncTray;

internal sealed class CommandPipeServer : IDisposable
{
    private readonly Func<TrayCommandRequest, Task<TrayCommandResponse>> _handler;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _listenerTask;

    public CommandPipeServer(Func<TrayCommandRequest, Task<TrayCommandResponse>> handler)
    {
        _handler = handler;
        _listenerTask = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    CommandProtocol.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cancellation.Token);
                await HandleClientAsync(pipe, _cancellation.Token);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                await Task.Delay(250, _cancellation.Token).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private async Task HandleClientAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        TrayCommandResponse response;
        try
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var line = await ReadLimitedLineAsync(reader, 4096, requestTimeout.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidDataException("命令为空。");
            }

            var request = JsonSerializer.Deserialize<TrayCommandRequest>(line, CommandProtocol.JsonOptions)
                ?? throw new InvalidDataException("命令为空。");
            response = await _handler(request);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response = new TrayCommandResponse(false, "读取命令超时。");
        }
        catch (Exception ex)
        {
            response = new TrayCommandResponse(false, $"命令处理失败：{ex.Message}");
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response, CommandProtocol.JsonOptions));
    }

    private static async Task<string?> ReadLimitedLineAsync(
        TextReader reader,
        int maxLength,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }
            if (buffer[0] == '\n')
            {
                return builder.ToString();
            }
            if (buffer[0] != '\r')
            {
                builder.Append(buffer[0]);
                if (builder.Length > maxLength)
                {
                    throw new InvalidDataException($"命令长度超过 {maxLength} 字符限制。");
                }
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
