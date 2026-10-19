using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace AIContextSyncTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly TunnelController _controller = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Control _commandDispatcher = new();
    private readonly CommandPipeServer _commandServer;
    private readonly Bitmap _baseBitmap;
    private readonly Dictionary<ServiceState, Icon> _icons;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly Dictionary<string, ServiceMenuItems> _serviceMenus = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolStripMenuItem> _monitorItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _startWithTrayItem;
    private readonly ToolStripMenuItem _notificationItem;
    private StatusForm? _statusForm;
    private ServiceSnapshot _lastSnapshot = new(
        ServiceState.Checking,
        "正在检查服务状态",
        "请稍候。",
        Array.Empty<SingleServiceSnapshot>());
    private bool _operationInProgress;
    private bool _exiting;

    public TrayApplicationContext()
    {
        _commandDispatcher.CreateControl();
        _baseBitmap = IconFactory.LoadBaseBitmap();
        _icons = Enum.GetValues<ServiceState>()
            .ToDictionary(state => state, state => IconFactory.CreateTrayIcon(_baseBitmap, state));

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Icon = _icons[ServiceState.Checking],
            Text = "AI 上下文同步：正在检查..."
        };

        var menu = new ContextMenuStrip
        {
            Renderer = new ModernMenuRenderer(),
            DropShadowEnabled = false,
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = new Font("Segoe UI", 9.25f)
        };

        _statusItem = new ToolStripMenuItem("● 正在检查服务状态...")
        {
            Enabled = false,
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };
        foreach (var def in AppPaths.Services)
        {
            var rootItem = new ToolStripMenuItem(GetServiceMenuTitle(def, null));
            var stateItem = new ToolStripMenuItem("状态：正在检查...") { Enabled = false };
            var startItem = new ToolStripMenuItem("启动", null, async (_, _) => await RunServiceOperationAsync(def.Id, Operation.Start));
            var stopItem = new ToolStripMenuItem("停止", null, async (_, _) => await RunServiceOperationAsync(def.Id, Operation.Stop));
            var restartItem = new ToolStripMenuItem("重启", null, async (_, _) => await RunServiceOperationAsync(def.Id, Operation.Restart));
            var openUiItem = new ToolStripMenuItem($"打开管理界面 ({def.HealthPort})", null, (_, _) => OpenUrl($"http://127.0.0.1:{def.HealthPort}/ui"));
            var showLogItem = new ToolStripMenuItem("查看状态与日志", null, async (_, _) => await ShowStatusFormAsync(def.Id));

            rootItem.DropDownItems.Add(stateItem);
            rootItem.DropDownItems.Add(new ToolStripSeparator());
            rootItem.DropDownItems.Add(startItem);
            rootItem.DropDownItems.Add(stopItem);
            rootItem.DropDownItems.Add(restartItem);
            rootItem.DropDownItems.Add(new ToolStripSeparator());
            rootItem.DropDownItems.Add(openUiItem);
            rootItem.DropDownItems.Add(showLogItem);
            _serviceMenus[def.Id] = new ServiceMenuItems(rootItem, stateItem, startItem, stopItem, restartItem, openUiItem);
        }

        _startItem = new ToolStripMenuItem("启动已选服务", null, async (_, _) => await RunOperationAsync(Operation.Start));
        _stopItem = new ToolStripMenuItem("停止已选服务", null, async (_, _) => await RunOperationAsync(Operation.Stop));
        _restartItem = new ToolStripMenuItem("重启已选服务", null, async (_, _) => await RunOperationAsync(Operation.Restart));
        var refreshItem = new ToolStripMenuItem("刷新状态", null, async (_, _) => await RefreshStatusAsync());
        var allServicesItem = new ToolStripMenuItem("已选服务");
        allServicesItem.DropDownItems.AddRange([_startItem, _stopItem, _restartItem, new ToolStripSeparator(), refreshItem]);

        var relatedItem = new ToolStripMenuItem("相关入口");
        relatedItem.DropDownItems.Add(new ToolStripMenuItem("打开 ChatGPT Apps", null, (_, _) => OpenUrl("https://chatgpt.com/plugins")));
        relatedItem.DropDownItems.Add(new ToolStripMenuItem("打开 OpenAI Tunnel 设置", null, (_, _) => OpenUrl("https://platform.openai.com/settings/organization/tunnels")));
        relatedItem.DropDownItems.Add(new ToolStripMenuItem("打开日志目录", null, (_, _) => OpenDirectory(AppPaths.LogDirectory)));
        relatedItem.DropDownItems.Add(new ToolStripMenuItem("查看全部状态与日志", null, async (_, _) => await ShowStatusFormAsync()));

        _autoStartItem = new ToolStripMenuItem("登录后启动托盘", null, (_, _) => ToggleAutoStart())
        {
            Checked = ShortcutManager.IsAutoStartEnabled()
        };
        _startWithTrayItem = new ToolStripMenuItem("托盘启动时自动连接")
        {
            Checked = _settings.StartServiceWithTray
        };
        _startWithTrayItem.Click += (_, _) =>
        {
            _settings.StartServiceWithTray = !_settings.StartServiceWithTray;
            _startWithTrayItem.Checked = _settings.StartServiceWithTray;
            _settings.Save();
        };
        _notificationItem = new ToolStripMenuItem("显示状态通知")
        {
            Checked = _settings.ShowNotifications
        };
        _notificationItem.Click += (_, _) =>
        {
            _settings.ShowNotifications = !_settings.ShowNotifications;
            _notificationItem.Checked = _settings.ShowNotifications;
            _settings.Save();
        };
        var monitoredServicesItem = new ToolStripMenuItem("监测的 MCP");
        foreach (var def in AppPaths.Services)
        {
            var monitorItem = new ToolStripMenuItem(GetServiceMenuTitle(def, null))
            {
                Checked = !_settings.UnmonitoredServiceIds.Contains(def.Id),
                CheckOnClick = false
            };
            monitorItem.Click += async (_, _) => await ToggleServiceMonitoringAsync(def.Id);
            _monitorItems[def.Id] = monitorItem;
            monitoredServicesItem.DropDownItems.Add(monitorItem);
        }

        var settingsItem = new ToolStripMenuItem("设置");
        settingsItem.DropDownItems.AddRange([
            monitoredServicesItem,
            new ToolStripSeparator(),
            _autoStartItem,
            _startWithTrayItem,
            _notificationItem
        ]);

        var exitItem = new ToolStripMenuItem("退出托盘程序", null, (_, _) => ExitThread());

        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        foreach (var def in AppPaths.Services)
        {
            menu.Items.Add(_serviceMenus[def.Id].Root);
        }
        menu.Items.Add(allServicesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(relatedItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        _notifyIcon.ContextMenuStrip = menu;

        _notifyIcon.MouseDoubleClick += async (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                await ShowStatusFormAsync();
            }
        };

        _statusTimer = new System.Windows.Forms.Timer { Interval = 15_000 };
        _statusTimer.Tick += async (_, _) => await MonitorAndRepairAsync();
        _statusTimer.Start();
        _commandServer = new CommandPipeServer(DispatchCommandAsync);

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        ShortcutManager.InstallStartMenuShortcut();
        await MonitorAndRepairAsync();
    }

    private async Task MonitorAndRepairAsync()
    {
        var snapshot = await RefreshStatusAsync();
        if (!_settings.StartServiceWithTray || _operationInProgress)
        {
            return;
        }

        foreach (var service in snapshot.Services.Where(service =>
                     service.IsMonitored &&
                     service.State == ServiceState.Stopped &&
                     !_settings.DisabledServiceIds.Contains(service.Id)))
        {
            await RunServiceOperationAsync(service.Id, Operation.Start, showSuccessNotification: false);
        }
    }

    private Task<ServiceSnapshot> RefreshStatusAsync() => RefreshStatusCoreAsync(waitForCurrentRefresh: false);

    private Task<ServiceSnapshot> RefreshStatusForCommandAsync() => RefreshStatusCoreAsync(waitForCurrentRefresh: true);

    private async Task<ServiceSnapshot> RefreshStatusCoreAsync(bool waitForCurrentRefresh)
    {
        if (_operationInProgress)
        {
            return _lastSnapshot;
        }

        var entered = waitForCurrentRefresh
            ? await _refreshLock.WaitAsync(TimeSpan.FromSeconds(10))
            : await _refreshLock.WaitAsync(0);
        if (!entered)
        {
            return _lastSnapshot;
        }

        try
        {
            var snapshot = await _controller.GetStatusAsync(GetMonitoredServiceIds());
            _lastSnapshot = snapshot;
            ApplySnapshot(snapshot);
            if (_statusForm is { IsDisposed: false })
            {
                _statusForm.ApplySnapshot(snapshot);
            }
            return snapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void ApplySnapshot(ServiceSnapshot snapshot)
    {
        _notifyIcon.Icon = _icons[snapshot.State];
        _notifyIcon.Text = snapshot.State switch
        {
            ServiceState.Checking => "AI 上下文同步：正在检查...",
            _ => $"AI 上下文同步：{snapshot.Summary}"
        };
        _statusItem.Text = snapshot.State switch
        {
            ServiceState.Degraded => $"▲ {snapshot.Summary}",
            ServiceState.Checking => "● 正在检查服务状态...",
            _ => $"● {snapshot.Summary}"
        };
        var monitoredServices = snapshot.Services.Where(service => service.IsMonitored).ToList();
        var hasMonitoredServices = monitoredServices.Count > 0;
        _startItem.Enabled = hasMonitoredServices && monitoredServices.Any(service => service.State != ServiceState.Connected) && !_operationInProgress;
        _stopItem.Enabled = hasMonitoredServices && monitoredServices.Any(service => service.State != ServiceState.Stopped) && !_operationInProgress;
        _restartItem.Enabled = hasMonitoredServices && monitoredServices.Any(service => service.State != ServiceState.Stopped) && !_operationInProgress;

        foreach (var service in snapshot.Services)
        {
            if (!_serviceMenus.TryGetValue(service.Id, out var items))
            {
                continue;
            }

            var disabled = _settings.DisabledServiceIds.Contains(service.Id);
            var displaySummary = !service.IsMonitored
                ? "未监测"
                : disabled && service.State == ServiceState.Stopped
                    ? "已停用"
                    : service.Summary;
            items.Root.Text = GetServiceMenuTitle(
                AppPaths.Services.First(def => def.Id.Equals(service.Id, StringComparison.OrdinalIgnoreCase)),
                displaySummary);
            items.State.Text = $"状态：{displaySummary}";
            items.Start.Enabled = service.IsMonitored && service.State != ServiceState.Connected && !_operationInProgress;
            items.Stop.Enabled = service.IsMonitored && service.State != ServiceState.Stopped && !_operationInProgress;
            items.Restart.Enabled = service.IsMonitored && service.State != ServiceState.Stopped && !_operationInProgress;
            items.OpenUi.Enabled = service.IsMonitored && service.LocalHealthy;
            if (_monitorItems.TryGetValue(service.Id, out var monitorItem))
            {
                monitorItem.Checked = service.IsMonitored;
                monitorItem.Enabled = !_operationInProgress;
            }
        }
    }

    private async Task<(bool Success, string Message)> RunOperationAsync(Operation operation, bool showSuccessNotification = true)
    {
        if (_operationInProgress)
        {
            return (false, "已有服务操作正在执行。");
        }

        _operationInProgress = true;
        (bool Success, string Message) result = (false, "操作未完成。");
        _lastSnapshot = new ServiceSnapshot(ServiceState.Checking, "正在执行操作", "请稍候。", _lastSnapshot.Services);
        ApplySnapshot(_lastSnapshot);
        try
        {
            var targetIds = GetMonitoredServiceIds();
            if (targetIds.Count == 0)
            {
                return (false, "未选择需要操作的 MCP。");
            }

            if (operation == Operation.Stop)
            {
                _settings.DisabledServiceIds.UnionWith(targetIds);
            }
            else
            {
                _settings.DisabledServiceIds.ExceptWith(targetIds);
            }
            _settings.Save();

            result = operation switch
            {
                Operation.Start => await _controller.StartAsync(targetIds, TimeSpan.FromSeconds(5)),
                Operation.Stop => await _controller.StopAsync(targetIds),
                Operation.Restart => await _controller.RestartAsync(targetIds),
                _ => (false, "未知操作。")
            };

            if (_settings.ShowNotifications && (showSuccessNotification || !result.Success))
            {
                _notifyIcon.ShowBalloonTip(
                    3500,
                    result.Success ? "操作完成" : "操作失败",
                    result.Message,
                    result.Success ? ToolTipIcon.Info : ToolTipIcon.Error);
            }
        }
        finally
        {
            _operationInProgress = false;
            await RefreshStatusAsync();
        }
        return result;
    }

    private async Task<(bool Success, string Message)> RunServiceOperationAsync(
        string serviceId,
        Operation operation,
        bool showSuccessNotification = true)
    {
        if (_operationInProgress)
        {
            return (false, "已有服务操作正在执行。");
        }

        _operationInProgress = true;
        (bool Success, string Message) result = (false, "操作未完成。");
        _lastSnapshot = new ServiceSnapshot(ServiceState.Checking, "正在执行操作", "请稍候。", _lastSnapshot.Services);
        ApplySnapshot(_lastSnapshot);
        try
        {
            if (operation == Operation.Stop)
            {
                _settings.DisabledServiceIds.Add(serviceId);
            }
            else
            {
                _settings.DisabledServiceIds.Remove(serviceId);
                _settings.UnmonitoredServiceIds.Remove(serviceId);
            }
            _settings.Save();

            result = operation switch
            {
                Operation.Start => await _controller.StartServiceAsync(serviceId, TimeSpan.FromSeconds(5)),
                Operation.Stop => await _controller.StopServiceAsync(serviceId),
                Operation.Restart => await _controller.RestartServiceAsync(serviceId),
                _ => (false, "未知操作。")
            };

            if (_settings.ShowNotifications && (showSuccessNotification || !result.Success))
            {
                _notifyIcon.ShowBalloonTip(
                    3500,
                    result.Success ? "操作完成" : "操作失败",
                    result.Message,
                    result.Success ? ToolTipIcon.Info : ToolTipIcon.Error);
            }
        }
        finally
        {
            _operationInProgress = false;
            await RefreshStatusAsync();
        }
        return result;
    }

    private Task<TrayCommandResponse> DispatchCommandAsync(TrayCommandRequest request)
    {
        var completion = new TaskCompletionSource<TrayCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _commandDispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    completion.SetResult(await ExecuteCommandAsync(request));
                }
                catch (Exception ex)
                {
                    completion.SetResult(new TrayCommandResponse(false, $"命令执行失败：{ex.Message}"));
                }
            }));
        }
        catch (Exception ex)
        {
            completion.SetResult(new TrayCommandResponse(false, $"无法调度命令：{ex.Message}"));
        }
        return completion.Task;
    }

    private async Task<TrayCommandResponse> ExecuteCommandAsync(TrayCommandRequest request)
    {
        var action = request.Action.Trim().ToLowerInvariant();
        var target = request.Target.Trim().ToLowerInvariant();
        if (target is not ("filesystem" or "commander" or "all"))
        {
            return new TrayCommandResponse(false, $"不支持的服务：{request.Target}");
        }

        if (action == "status")
        {
            var status = await RefreshStatusForCommandAsync();
            return new TrayCommandResponse(true, "状态检查完成。", ToDto(status, target));
        }

        var operation = action switch
        {
            "start" => Operation.Start,
            "stop" => Operation.Stop,
            "restart" => Operation.Restart,
            _ => (Operation?)null
        };
        if (!operation.HasValue)
        {
            return new TrayCommandResponse(false, $"不支持的操作：{request.Action}");
        }

        var result = target == "all"
            ? await RunOperationAsync(operation.Value, showSuccessNotification: false)
            : await RunServiceOperationAsync(target, operation.Value, showSuccessNotification: false);
        var snapshot = await RefreshStatusAsync();
        return new TrayCommandResponse(result.Success, result.Message, ToDto(snapshot, target));
    }

    private static ServiceSnapshotDto ToDto(ServiceSnapshot snapshot, string target)
    {
        var services = target == "all"
            ? snapshot.Services
            : snapshot.Services.Where(service => service.Id.Equals(target, StringComparison.OrdinalIgnoreCase)).ToList();
        var selected = target == "all" ? null : services.SingleOrDefault();
        return new ServiceSnapshotDto(
            (selected?.State ?? snapshot.State).ToString().ToLowerInvariant(),
            selected?.Summary ?? snapshot.Summary,
            selected?.Details ?? snapshot.Details,
            services.Select(service => new SingleServiceSnapshotDto(
                service.Id,
                service.Name,
                service.Port,
                service.IsMonitored,
                service.State.ToString().ToLowerInvariant(),
                service.Summary,
                service.Details,
                service.TunnelProcessId,
                service.McpProcessId,
                service.LocalHealthy,
                service.LocalReady,
                service.ControlPlaneReachable,
                service.CurrentLogPath)).ToList());
    }

    private HashSet<string> GetMonitoredServiceIds() => AppPaths.Services
        .Where(service => !_settings.UnmonitoredServiceIds.Contains(service.Id))
        .Select(service => service.Id)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task ToggleServiceMonitoringAsync(string serviceId)
    {
        if (_operationInProgress)
        {
            return;
        }

        var isMonitored = !_settings.UnmonitoredServiceIds.Contains(serviceId);
        if (isMonitored)
        {
            var stopped = await RunServiceOperationAsync(serviceId, Operation.Stop);
            if (!stopped.Success)
            {
                return;
            }

            _settings.UnmonitoredServiceIds.Add(serviceId);
            _settings.Save();
            await RefreshStatusAsync();
            return;
        }

        _settings.UnmonitoredServiceIds.Remove(serviceId);
        _settings.DisabledServiceIds.Remove(serviceId);
        _settings.Save();
        await RefreshStatusAsync();
        if (_settings.StartServiceWithTray)
        {
            await RunServiceOperationAsync(serviceId, Operation.Start);
        }
    }

    private void ToggleAutoStart()
    {
        var target = !_autoStartItem.Checked;
        var result = ShortcutManager.SetAutoStart(target);
        if (result.Success)
        {
            _autoStartItem.Checked = target;
        }
        _notifyIcon.ShowBalloonTip(
            3000,
            result.Success ? "设置成功" : "设置失败",
            result.Message,
            result.Success ? ToolTipIcon.Info : ToolTipIcon.Error);
    }

    private async Task ShowStatusFormAsync(string? serviceId = null)
    {
        if (_statusForm is null || _statusForm.IsDisposed)
        {
            _statusForm = new StatusForm(_controller, RefreshStatusAsync, _icons[ServiceState.Connected]);
            _statusForm.Show();
        }
        else
        {
            _statusForm.BringToFront();
        }

        if (serviceId is not null)
        {
            _statusForm.SelectLogSource(serviceId);
        }
        await _statusForm.RefreshDataAsync();
    }

    private static string GetServiceMenuTitle(TunnelServiceDefinition definition, string? status)
    {
        var shortName = definition.Id.Equals("filesystem", StringComparison.OrdinalIgnoreCase)
            ? "文件服务"
            : "终端命令";
        return string.IsNullOrWhiteSpace(status) ? shortName : $"{shortName} · {status}";
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private static void OpenDirectory(string directoryPath)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = directoryPath,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    protected override void ExitThreadCore()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _commandServer.Dispose();
        _commandDispatcher.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _statusForm?.Dispose();
        _controller.Dispose();
        _refreshLock.Dispose();
        _baseBitmap.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        base.ExitThreadCore();
    }

    private sealed record ServiceMenuItems(
        ToolStripMenuItem Root,
        ToolStripMenuItem State,
        ToolStripMenuItem Start,
        ToolStripMenuItem Stop,
        ToolStripMenuItem Restart,
        ToolStripMenuItem OpenUi);

    private enum Operation
    {
        Start,
        Stop,
        Restart
    }
}
