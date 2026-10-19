using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Security.Principal;

namespace AIContextSyncTray;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var parsed = ParseArguments(args);
        if (!parsed.Success)
        {
            Console.Error.WriteLine(parsed.Message);
            PrintUsage();
            return 2;
        }

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                CommandProtocol.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Impersonation);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await pipe.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var request = new TrayCommandRequest(parsed.Action!, parsed.Target!);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, CommandProtocol.JsonOptions));
            var responseLine = await reader.ReadLineAsync(timeout.Token);
            var response = responseLine is null
                ? null
                : JsonSerializer.Deserialize<TrayCommandResponse>(responseLine, CommandProtocol.JsonOptions);
            if (response is null)
            {
                Console.Error.WriteLine("托盘未返回有效响应。");
                return 3;
            }

            if (parsed.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(response, CommandProtocol.JsonOptions));
            }
            else
            {
                PrintHumanReadable(response);
            }
            return response.Success ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("连接托盘或等待操作结果超时。");
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"无法连接正在运行的托盘：{ex.Message}");
            return 3;
        }
    }

    private static (bool Success, string? Action, string? Target, bool Json, string Message) ParseArguments(string[] args)
    {
        var values = args.Where(arg => !arg.Equals("--json", StringComparison.OrdinalIgnoreCase)).ToArray();
        var json = args.Any(arg => arg.Equals("--json", StringComparison.OrdinalIgnoreCase));
        if (values.Length != 3 || !values[0].Equals("service", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, null, json, "参数格式错误。");
        }

        var action = values[1].ToLowerInvariant();
        var target = values[2].ToLowerInvariant();
        if (action is not ("status" or "start" or "stop" or "restart"))
        {
            return (false, null, null, json, $"不支持的操作：{values[1]}");
        }
        if (target is not ("filesystem" or "commander" or "all"))
        {
            return (false, null, null, json, $"不支持的服务：{values[2]}");
        }
        return (true, action, target, json, string.Empty);
    }

    private static void PrintHumanReadable(TrayCommandResponse response)
    {
        Console.WriteLine(response.Message);
        if (response.Snapshot is null)
        {
            return;
        }
        Console.WriteLine($"总体：{response.Snapshot.Summary}");
        foreach (var service in response.Snapshot.Services)
        {
            Console.WriteLine(
                $"{service.Id}: {service.Summary}; monitored={service.IsMonitored}; Tunnel PID={service.TunnelProcessId?.ToString() ?? "-"}; " +
                $"MCP PID={service.McpProcessId?.ToString() ?? "-"}; health={service.LocalHealthy}; " +
                $"ready={service.LocalReady}; remote={service.ControlPlaneReachable}");
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("用法：AIContextSyncCtl service <status|start|stop|restart> <filesystem|commander|all> [--json]");
    }
}
