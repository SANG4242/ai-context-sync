using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace AIContextSyncTray;

internal sealed class TunnelController : IDisposable
{
    private static readonly TimeSpan LocalCheckTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ControlPlaneCheckTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(25);
    private readonly HttpClient _localClient = new() { Timeout = LocalCheckTimeout };
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Dictionary<string, Process> _startedProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _activeLogPaths = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ServiceSnapshot> GetStatusAsync(
        IReadOnlySet<string> monitoredServiceIds,
        CancellationToken cancellationToken = default)
    {
        var items = (await Task.WhenAll(AppPaths.Services.Select(def =>
                monitoredServiceIds.Contains(def.Id)
                    ? GetServiceStatusAsync(def, cancellationToken)
                    : Task.FromResult(CreateUnmonitoredSnapshot(def)))))
            .ToList();
        var monitoredItems = items.Where(service => service.IsMonitored).ToList();
        var connectedCount = monitoredItems.Count(service => service.State == ServiceState.Connected);
        var stoppedCount = monitoredItems.Count(service => service.State == ServiceState.Stopped);

        ServiceState overallState;
        string overallSummary;
        string overallDetails;

        if (monitoredItems.Count == 0)
        {
            overallState = ServiceState.Stopped;
            overallSummary = "未选择监测服务";
            overallDetails = "可在设置中选择需要启用和监测的 MCP。";
        }
        else if (connectedCount == monitoredItems.Count)
        {
            overallState = ServiceState.Connected;
            overallSummary = $"已选服务全部连接 ({connectedCount}/{monitoredItems.Count})";
            overallDetails = "已选 Tunnel 与 MCP 服务均正常就绪。";
        }
        else if (stoppedCount == monitoredItems.Count)
        {
            overallState = ServiceState.Stopped;
            overallSummary = "已选服务全部停止";
            overallDetails = "未检测到已选服务正在运行。";
        }
        else
        {
            overallState = ServiceState.Degraded;
            overallSummary = $"已选服务部分在线 ({connectedCount}/{monitoredItems.Count})";
            overallDetails = string.Join("；", monitoredItems.Select(item => $"{item.Name}: {item.Summary}"));
        }

        return new ServiceSnapshot(overallState, overallSummary, overallDetails, items);
    }

    private static SingleServiceSnapshot CreateUnmonitoredSnapshot(TunnelServiceDefinition def) => new(
        def.Id,
        def.Name,
        def.HealthPort,
        false,
        ServiceState.Stopped,
        "未监测",
        "此服务已从监测、自动连接和批量操作中排除。",
        null,
        null,
        false,
        false,
        false,
        null);

    private async Task<SingleServiceSnapshot> GetServiceStatusAsync(
        TunnelServiceDefinition def,
        CancellationToken cancellationToken)
    {
        var healthy = await IsEndpointOkAsync($"http://127.0.0.1:{def.HealthPort}/healthz", cancellationToken);
        var ready = healthy && await IsEndpointOkAsync($"http://127.0.0.1:{def.HealthPort}/readyz", cancellationToken);
        var remote = ready && await CanReachControlPlaneAsync(def.ProfilePath, cancellationToken);
        var tunnelPid = FindProcessIdForPort(def.HealthPort);
        var mcpPid = tunnelPid.HasValue
            ? ProcessTreeHelper.FindDescendantProcessId(tunnelPid.Value, "node")
            : null;

        _activeLogPaths.TryGetValue(def.Id, out var logPath);
        if (string.IsNullOrWhiteSpace(logPath))
        {
            logPath = GetLatestLogFile(def.Id)?.FullName;
        }

        var state = !healthy && !tunnelPid.HasValue
            ? ServiceState.Stopped
            : healthy && ready && remote && mcpPid.HasValue
                ? ServiceState.Connected
                : ServiceState.Degraded;
        var summary = state switch
        {
            ServiceState.Stopped => "未运行",
            ServiceState.Connected => "已连接",
            _ => "连接异常"
        };
        var details = state switch
        {
            ServiceState.Stopped => $"{def.Name} 进程未启动。",
            ServiceState.Connected => "本地 MCP 已就绪，OpenAI Tunnel 控制面可访问。",
            _ when !healthy => "健康检查失败。",
            _ when !ready => "本地 MCP 尚未就绪。",
            _ when !mcpPid.HasValue => "Tunnel 已运行，但未检测到 MCP 子进程。",
            _ => "本地已就绪，但 OpenAI Tunnel 控制面不可访问。"
        };

        return new SingleServiceSnapshot(
            def.Id,
            def.Name,
            def.HealthPort,
            true,
            state,
            summary,
            details,
            tunnelPid,
            mcpPid,
            healthy,
            ready,
            remote,
            logPath);
    }

    public Task<(bool Success, string Message)> StartAsync(
        IEnumerable<string> serviceIds,
        TimeSpan proxyWait,
        CancellationToken cancellationToken = default) =>
        StartServicesAsync(serviceIds, proxyWait, cancellationToken);

    public Task<(bool Success, string Message)> StartServiceAsync(
        string serviceId,
        TimeSpan proxyWait,
        CancellationToken cancellationToken = default) =>
        StartServicesAsync([serviceId], proxyWait, cancellationToken);

    private async Task<(bool Success, string Message)> StartServicesAsync(
        IEnumerable<string> serviceIds,
        TimeSpan proxyWait,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var targets = ResolveServices(serviceIds);
            if (targets.Count == 0)
            {
                return (false, "未找到目标服务。");
            }
            if (!File.Exists(AppPaths.TunnelClientPath))
            {
                return (false, $"未找到 Tunnel Client：{AppPaths.TunnelClientPath}");
            }

            foreach (var target in targets)
            {
                if (!File.Exists(target.ProfilePath))
                {
                    return (false, $"未找到服务配置：{target.ProfilePath}");
                }
                var proxy = ReadYamlValue(File.ReadAllText(target.ProfilePath), "http_proxy");
                if (string.IsNullOrWhiteSpace(proxy)) continue;
                if (!Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri))
                {
                    return (false, $"{target.Name} 的 http_proxy 格式无效。");
                }
                if (!await WaitForPortAsync(proxyUri.Host, proxyUri.Port, proxyWait, cancellationToken))
                {
                    return (false, $"{target.Name} 的代理端口未就绪。");
                }
            }

            Directory.CreateDirectory(AppPaths.LogDirectory);
            RotateLogs();

            var launched = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in targets)
            {
                if (!File.Exists(def.ProfilePath))
                {
                    return (false, $"未找到服务配置：{def.ProfilePath}");
                }

                var existingPid = FindProcessIdForPort(def.HealthPort);
                var isHealthy = await IsEndpointOkAsync($"http://127.0.0.1:{def.HealthPort}/healthz", cancellationToken);
                if (existingPid.HasValue && isHealthy)
                {
                    continue;
                }
                if (existingPid.HasValue)
                {
                    return (false, $"{def.Name} 的端口 {def.HealthPort} 已被异常进程占用，请先停止该进程。");
                }

                var logPath = Path.Combine(
                    AppPaths.LogDirectory,
                    $"{def.LogPrefix}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                _activeLogPaths[def.Id] = logPath;

                var proc = Process.Start(CreateStartInfo(def, logPath));
                if (proc is null)
                {
                    return (false, $"无法启动 {def.Name}。");
                }

                _startedProcesses[def.Id] = proc;
                launched[def.Id] = proc;
            }

            var deadline = DateTime.UtcNow + StartupTimeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var pair in launched)
                {
                    pair.Value.Refresh();
                    if (pair.Value.HasExited)
                    {
                        var name = targets.First(service => service.Id == pair.Key).Name;
                        return (false, $"{name} 启动失败，退出码 {pair.Value.ExitCode}。请查看对应日志。");
                    }
                }

                if (await AreServicesLocallyReadyAsync(targets, cancellationToken))
                {
                    var states = await Task.WhenAll(targets.Select(target => GetServiceStatusAsync(target, cancellationToken)));
                    if (states.All(state => state.State == ServiceState.Connected))
                    {
                        return (true, targets.Count == AppPaths.Services.Count
                            ? "所有 Tunnel 服务已成功连接。"
                            : $"{targets[0].Name} 已成功连接。");
                    }
                }

                var delay = deadline - DateTime.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, delay.TotalMilliseconds)), cancellationToken);
                }
            }

            var finalStates = await Task.WhenAll(targets.Select(target => GetServiceStatusAsync(target, cancellationToken)));
            var details = string.Join("；", finalStates.Select(state => $"{state.Name}: {state.Summary}"));
            return finalStates.All(state => state.State == ServiceState.Connected)
                ? (true, "目标服务已成功启动。")
                : (false, $"启动在 {StartupTimeout.TotalSeconds:0} 秒内未完成：{details}");
        }
        catch (OperationCanceledException)
        {
            return (false, "启动操作已取消。");
        }
        catch (Exception ex)
        {
            return (false, $"启动失败：{ex.Message}");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<(bool Success, string Message)> StopAsync(
        IEnumerable<string> serviceIds,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var targets = ResolveServices(serviceIds);
            if (targets.Count == 0)
            {
                return (false, "未选择需要停止的服务。");
            }

            var pids = targets
                .Select(def => FindProcessIdForPort(def.HealthPort))
                .Where(pid => pid.HasValue)
                .Select(pid => pid!.Value)
                .ToHashSet();

            foreach (var pid in pids)
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }

            foreach (var target in targets)
            {
                if (_startedProcesses.Remove(target.Id, out var startedProcess))
                {
                    startedProcess.Dispose();
                }
            }

            for (var i = 0; i < 20; i++)
            {
                var remaining = targets.Any(service => FindProcessIdForPort(service.HealthPort).HasValue);
                if (!remaining)
                {
                    return (true, "已选 Tunnel 与 MCP 服务均已停止。");
                }
                await Task.Delay(250, cancellationToken);
            }

            return (false, "部分已选服务停止超时，请检查残留进程。");
        }
        catch (Exception ex)
        {
            return (false, $"停止失败：{ex.Message}");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<(bool Success, string Message)> StopServiceAsync(
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var service = ResolveServices([serviceId]).SingleOrDefault();
            if (service is null)
            {
                return (false, "未找到目标服务。");
            }

            var pid = FindProcessIdForPort(service.HealthPort);
            if (!pid.HasValue)
            {
                return (true, $"{service.Name} 已停止。");
            }

            try
            {
                using var proc = Process.GetProcessById(pid.Value);
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                return (false, $"停止 {service.Name} 失败：{ex.Message}");
            }

            if (_startedProcesses.Remove(service.Id, out var startedProcess))
            {
                startedProcess.Dispose();
            }

            for (var i = 0; i < 20; i++)
            {
                if (!FindProcessIdForPort(service.HealthPort).HasValue)
                {
                    return (true, $"{service.Name} 已停止。");
                }
                await Task.Delay(250, cancellationToken);
            }

            return (false, $"{service.Name} 停止超时，请检查残留进程。");
        }
        catch (Exception ex)
        {
            return (false, $"停止失败：{ex.Message}");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<(bool Success, string Message)> RestartAsync(
        IEnumerable<string> serviceIds,
        CancellationToken cancellationToken = default)
    {
        var targets = serviceIds.ToArray();
        var stopped = await StopAsync(targets, cancellationToken);
        if (!stopped.Success)
        {
            return stopped;
        }

        await Task.Delay(750, cancellationToken);
        return await StartAsync(targets, TimeSpan.FromSeconds(5), cancellationToken);
    }

    public async Task<(bool Success, string Message)> RestartServiceAsync(
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        var stopped = await StopServiceAsync(serviceId, cancellationToken);
        if (!stopped.Success)
        {
            return stopped;
        }

        await Task.Delay(750, cancellationToken);
        return await StartServiceAsync(serviceId, TimeSpan.FromSeconds(5), cancellationToken);
    }

    public string ReadRecentLogs(string? serviceId = null, int maxLines = 250)
    {
        var latest = GetLatestLogFile(serviceId);
        if (latest is null)
        {
            return "尚无运行日志。";
        }

        try
        {
            var queue = new Queue<string>(maxLines);
            using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (queue.Count >= maxLines)
                {
                    queue.Dequeue();
                }
                queue.Enqueue(line);
            }

            return string.Join(Environment.NewLine, queue);
        }
        catch (Exception ex)
        {
            return $"读取日志失败：{ex.Message}";
        }
    }

    public FileInfo? GetLatestLogFile(string? serviceId = null)
    {
        if (!Directory.Exists(AppPaths.LogDirectory))
        {
            return null;
        }

        var dir = new DirectoryInfo(AppPaths.LogDirectory);
        var pattern = "*.log";

        if (!string.IsNullOrWhiteSpace(serviceId))
        {
            var def = AppPaths.Services.FirstOrDefault(s => s.Id.Equals(serviceId, StringComparison.OrdinalIgnoreCase));
            if (def is not null)
            {
                pattern = $"{def.LogPrefix}*.log";
            }
        }

        return dir.GetFiles(pattern)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    public static int? FindProcessIdForPort(int port)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c netstat -ano -p tcp | findstr /C:\"LISTENING\" | findstr /C:\":{port}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(startInfo);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = Regex.Split(line.Trim(), @"\s+");
                if (parts.Length >= 5 && parts[1].EndsWith($":{port}", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[^1], out var pid))
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        var processPath = p.MainModule?.FileName;
                        if (p.ProcessName.Equals("tunnel-client", StringComparison.OrdinalIgnoreCase) &&
                            processPath is not null &&
                            Path.GetFullPath(processPath).Equals(
                                Path.GetFullPath(AppPaths.TunnelClientPath),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return pid;
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static ProcessStartInfo CreateStartInfo(TunnelServiceDefinition def, string logPath)
    {
        var profile = File.ReadAllText(def.ProfilePath);
        var baseUrl = ReadYamlValue(profile, "base_url") ?? "https://api.openai.com";
        var proxyUrl = ReadYamlValue(profile, "http_proxy");
        var tunnelId = ReadYamlValue(profile, "tunnel_id");
        var keyReference = ReadYamlValue(profile, "api_key");
        if (keyReference is not null && !keyReference.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && !keyReference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("api_key 必须使用 file: 或 env: 引用。");
        }
        var listenAddress = ReadYamlValue(profile, "listen_addr") ?? $"127.0.0.1:{def.HealthPort}";
        var mcpCommand = ReadYamlCommand(profile);

        if (string.IsNullOrWhiteSpace(tunnelId) ||
            string.IsNullOrWhiteSpace(keyReference) ||
            string.IsNullOrWhiteSpace(mcpCommand))
        {
            throw new InvalidDataException($"{def.Name} 配置缺少 tunnel_id、api_key 或 MCP command。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = AppPaths.TunnelClientPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppPaths.ProjectRoot
        };
        AddArgument(startInfo, "run");
        AddOption(startInfo, "--profile-file", def.ProfilePath);
        AddOption(startInfo, "--control-plane.base-url", baseUrl);
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            AddOption(startInfo, "--control-plane.http-proxy", proxyUrl);
        }
        AddOption(startInfo, "--control-plane.tunnel-id", tunnelId);
        AddOption(startInfo, "--control-plane.api-key", keyReference);
        AddOption(startInfo, "--health.listen-addr", listenAddress);
        AddOption(startInfo, "--mcp.command", $"command={mcpCommand},channel=main");
        AddOption(startInfo, "--log.file", logPath);
        return startInfo;
    }

    private static void AddOption(ProcessStartInfo startInfo, string name, string value)
    {
        AddArgument(startInfo, name);
        AddArgument(startInfo, value);
    }

    private static void AddArgument(ProcessStartInfo startInfo, string value) => startInfo.ArgumentList.Add(value);

    private static async Task<bool> CanReachControlPlaneAsync(string profilePath, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(profilePath)) return false;
            var profile = File.ReadAllText(profilePath);
            var baseUrl = ReadYamlValue(profile, "base_url") ?? "https://api.openai.com";
            var proxyUrl = ReadYamlValue(profile, "http_proxy");
            var tunnelId = ReadYamlValue(profile, "tunnel_id");
            var keyReference = ReadYamlValue(profile, "api_key");

            if (string.IsNullOrWhiteSpace(tunnelId) || string.IsNullOrWhiteSpace(keyReference))
            {
                return false;
            }

            string? key = null;
            if (keyReference.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                var keyPath = Path.GetFullPath(keyReference[5..], AppPaths.ProjectRoot);
                if (File.Exists(keyPath)) key = (await File.ReadAllTextAsync(keyPath, cancellationToken)).Trim();
            }
            else if (keyReference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            {
                key = Environment.GetEnvironmentVariable(keyReference[4..])?.Trim();
            }
            if (string.IsNullOrWhiteSpace(key)) return false;

            using var handler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(proxyUrl))
            {
                handler.Proxy = new WebProxy(proxyUrl);
                handler.UseProxy = true;
            }

            using var client = new HttpClient(handler) { Timeout = ControlPlaneCheckTimeout };
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl.TrimEnd('/')}/v1/tunnels/{tunnelId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await client.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsEndpointOkAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _localClient.GetAsync(url, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> AreServicesLocallyReadyAsync(
        IReadOnlyCollection<TunnelServiceDefinition> services,
        CancellationToken cancellationToken)
    {
        var checks = services.Select(async def =>
            await IsEndpointOkAsync($"http://127.0.0.1:{def.HealthPort}/healthz", cancellationToken) &&
            await IsEndpointOkAsync($"http://127.0.0.1:{def.HealthPort}/readyz", cancellationToken));
        return (await Task.WhenAll(checks)).All(ready => ready);
    }

    private static IReadOnlyList<TunnelServiceDefinition> ResolveServices(IEnumerable<string> serviceIds)
    {
        var ids = serviceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AppPaths.Services.Where(service => ids.Contains(service.Id)).ToList();
    }

    private static async Task<bool> WaitForPortAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, cancellationToken);
                return true;
            }
            catch when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000, cancellationToken);
            }
        } while (DateTime.UtcNow < deadline);

        return false;
    }

    private static string? ReadYamlValue(string yaml, string key)
    {
        var match = Regex.Match(
            yaml,
            $"^\\s*{Regex.Escape(key)}\\s*:\\s*[\\\"']?(?<value>[^\\\"'\\r\\n]+)[\\\"']?\\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static string? ReadYamlCommand(string yaml)
    {
        var match = Regex.Match(
            yaml,
            "^\\s*(?:-\\s*)?command\\s*:\\s*(?<quote>['\\\"])(?<value>.+)\\k<quote>\\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static void RotateLogs()
    {
        var directory = new DirectoryInfo(AppPaths.LogDirectory);
        if (!directory.Exists)
        {
            return;
        }

        var files = directory.GetFiles("*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        foreach (var oversized in files.Where(file => file.Length > 10L * 1024 * 1024))
        {
            TryDelete(oversized);
        }

        files = directory.GetFiles("*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();
        foreach (var oldFile in files.Skip(10))
        {
            TryDelete(oldFile);
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                file.FullName,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _localClient.Dispose();
        _operationLock.Dispose();
        foreach (var pair in _startedProcesses)
        {
            pair.Value.Dispose();
        }
        _startedProcesses.Clear();
    }
}
