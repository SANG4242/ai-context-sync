using System.Diagnostics;

namespace AIContextSyncTray;

internal sealed record TunnelServiceDefinition(
    string Id,
    string Name,
    string ProfilePath,
    int HealthPort,
    string LogPrefix);

internal static class AppPaths
{
    public static string ProjectRoot { get; } = FindProjectRoot();
    public static string FilesystemProfilePath => Path.Combine(ProjectRoot, "config", "tunnel-client", "local-filesystem.yaml");
    public static string DesktopCommanderProfilePath => Path.Combine(ProjectRoot, "config", "tunnel-client", "local-desktop-commander.yaml");
    public static string ProfilePath => FilesystemProfilePath;
    public static string TunnelClientPath => Path.GetFullPath(
        Environment.GetEnvironmentVariable("AI_CONTEXT_SYNC_TUNNEL_CLIENT")
        ?? Path.Combine(ProjectRoot, "tools", "tunnel-client", "v0.0.14", "bin", "tunnel-client.exe"));
    public static string RuntimeRoot => Environment.GetEnvironmentVariable("AI_CONTEXT_SYNC_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIContextSync");
    public static string LogDirectory => Path.Combine(RuntimeRoot, "logs");
    public static string SettingsPath => Path.Combine(RuntimeRoot, "tray-settings.json");
    public static string StartMenuShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        "AI 上下文同步.lnk");
    public static string StartupShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "AI 上下文同步.lnk");
    public static string ExecutablePath => Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.Combine(AppContext.BaseDirectory, "AIContextSyncTray.exe");

    public static IReadOnlyList<TunnelServiceDefinition> Services { get; } =
    [
        new("filesystem", "文件服务 (Filesystem)", FilesystemProfilePath, 8080, "tunnel-client-filesystem"),
        new("commander", "终端命令 (Desktop Commander)", DesktopCommanderProfilePath, 8081, "tunnel-client-desktop-commander")
    ];

    private static string FindProjectRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("AI_CONTEXT_SYNC_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var profile = Path.Combine(current.FullName, "config", "tunnel-client", "local-filesystem.yaml");
            var tools = Path.Combine(current.FullName, "tools", "tunnel-client");
            if (File.Exists(profile) && Directory.Exists(tools))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
    }
}
