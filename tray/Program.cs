using System.Threading;
using System.Windows.Forms;

namespace AIContextSyncTray;

internal static class Program
{
    private const string MutexName = "Local\\AIContextSyncTray_SingleInstance_Mutex";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "AI 上下文同步托盘已经在后台运行。\n请查看任务栏通知区域。",
                "AI 上下文同步",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
