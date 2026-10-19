using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace AIContextSyncTray;

internal sealed class StatusForm : Form
{
    private readonly TunnelController _controller;
    private readonly Func<Task<ServiceSnapshot>> _statusProvider;
    private readonly Label _statusLabel;
    private readonly TextBox _logBox;
    private readonly Button _refreshButton;
    private readonly ComboBox _logSelector;

    public StatusForm(
        TunnelController controller,
        Func<Task<ServiceSnapshot>> statusProvider,
        Icon applicationIcon)
    {
        _controller = controller;
        _statusProvider = statusProvider;
        Icon = applicationIcon;
        Text = "AI 上下文同步 - 状态与日志";
        Size = new Size(820, 620);
        MinimumSize = new Size(680, 480);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(14)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.25f),
            Margin = new Padding(0, 0, 0, 10)
        };

        var logHeader = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 0, 0, 6)
        };

        var logLabel = new Label
        {
            Text = "日志源：",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold),
            Margin = new Padding(0, 5, 4, 0)
        };

        _logSelector = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 260,
            Font = new Font("Segoe UI", 9f)
        };
        _logSelector.Items.Add(new LogSelectorItem("全局最新日志", null));
        foreach (var def in AppPaths.Services)
        {
            _logSelector.Items.Add(new LogSelectorItem(def.Name, def.Id));
        }
        _logSelector.SelectedIndex = 0;
        _logSelector.SelectedIndexChanged += async (_, _) => await RefreshLogsAsync();

        logHeader.Controls.Add(logLabel);
        logHeader.Controls.Add(_logSelector);

        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9f),
            BackColor = Color.White
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0)
        };

        var closeButton = new Button { Text = "关闭", Size = new Size(85, 32) };
        closeButton.Click += (_, _) => Close();

        var openLogsButton = new Button { Text = "打开日志目录", Size = new Size(110, 32) };
        openLogsButton.Click += (_, _) => OpenPath(AppPaths.LogDirectory);

        var copyButton = new Button { Text = "复制日志", Size = new Size(90, 32) };
        copyButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_logBox.Text))
            {
                Clipboard.SetText(_logBox.Text);
            }
        };

        _refreshButton = new Button { Text = "刷新状态", Size = new Size(90, 32) };
        _refreshButton.Click += async (_, _) => await RefreshDataAsync();

        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(openLogsButton);
        buttons.Controls.Add(copyButton);
        buttons.Controls.Add(_refreshButton);

        layout.Controls.Add(_statusLabel, 0, 0);
        layout.Controls.Add(logHeader, 0, 1);
        layout.Controls.Add(_logBox, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
    }

    public async Task RefreshDataAsync()
    {
        _refreshButton.Enabled = false;
        try
        {
            var snapshot = await _statusProvider();
            ApplySnapshot(snapshot);
        }
        finally
        {
            _refreshButton.Enabled = true;
        }
    }

    public void ApplySnapshot(ServiceSnapshot snapshot)
    {
        var lines = new List<string>
        {
            $"综合状态：{snapshot.Summary}",
            $"总体说明：{snapshot.Details}",
            $"日志目录：{AppPaths.LogDirectory}",
            new string('-', 72)
        };

        foreach (var svc in snapshot.Services)
        {
            lines.Add($"【{svc.Name}】");
            lines.Add($"  监测：{(svc.IsMonitored ? "已启用" : "未启用")} | 状态：{svc.Summary} | 端口：{svc.Port} | Tunnel PID：{svc.TunnelProcessId?.ToString() ?? "未运行"} | MCP PID：{svc.McpProcessId?.ToString() ?? "未运行"}");
            if (svc.IsMonitored)
            {
                lines.Add($"  健康检查：{(svc.LocalHealthy ? "通过" : "未通过")} | MCP就绪：{(svc.LocalReady ? "通过" : "未通过")} | OpenAI控制面：{(svc.ControlPlaneReachable ? "正常" : "异常")}");
            }
            lines.Add($"  详情：{svc.Details}");
        }

        _statusLabel.Text = string.Join(Environment.NewLine, lines);
        _ = RefreshLogsAsync();
    }

    public void SelectLogSource(string? serviceId)
    {
        for (var i = 0; i < _logSelector.Items.Count; i++)
        {
            if (_logSelector.Items[i] is LogSelectorItem item &&
                string.Equals(item.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
            {
                _logSelector.SelectedIndex = i;
                return;
            }
        }
    }

    private Task RefreshLogsAsync()
    {
        var selected = _logSelector.SelectedItem as LogSelectorItem;
        _logBox.Text = _controller.ReadRecentLogs(selected?.ServiceId);
        _logBox.SelectionStart = _logBox.Text.Length;
        _logBox.ScrollToCaret();
        return Task.CompletedTask;
    }

    private static void OpenPath(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private sealed record LogSelectorItem(string DisplayName, string? ServiceId)
    {
        public override string ToString() => DisplayName;
    }
}
