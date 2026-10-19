using System.Runtime.InteropServices;

namespace AIContextSyncTray;

internal static class ShortcutManager
{
    public static bool IsAutoStartEnabled() => File.Exists(AppPaths.StartupShortcutPath);

    public static (bool Success, string Message) SetAutoStart(bool enabled)
    {
        try
        {
            if (enabled)
            {
                CreateShortcut(AppPaths.StartupShortcutPath, "AI 上下文同步托盘");
                return (true, "已启用登录后自动启动。");
            }

            if (File.Exists(AppPaths.StartupShortcutPath))
            {
                File.Delete(AppPaths.StartupShortcutPath);
            }
            return (true, "已关闭登录后自动启动。");
        }
        catch (Exception ex)
        {
            return (false, $"设置自动启动失败：{ex.Message}");
        }
    }

    public static (bool Success, string Message) InstallStartMenuShortcut()
    {
        try
        {
            CreateShortcut(AppPaths.StartMenuShortcutPath, "管理 AI 上下文 Tunnel 与 Filesystem MCP");
            return (true, "开始菜单快捷方式已创建。");
        }
        catch (Exception ex)
        {
            return (false, $"创建开始菜单快捷方式失败：{ex.Message}");
        }
    }

    private static void CreateShortcut(string shortcutPath, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("系统未提供 WScript.Shell。");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法创建 WScript.Shell。");
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = AppPaths.ExecutablePath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(AppPaths.ExecutablePath);
        shortcut.Description = description;
        shortcut.IconLocation = $"{AppPaths.ExecutablePath},0";
        shortcut.Save();
        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);
    }
}
