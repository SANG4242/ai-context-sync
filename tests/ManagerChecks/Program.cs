using System.Diagnostics;
using System.Reflection;
using AIContextSyncTray;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
Environment.SetEnvironmentVariable("AI_CONTEXT_SYNC_ROOT", root);
Environment.SetEnvironmentVariable("AI_CONTEXT_SYNC_TUNNEL_CLIENT", Path.Combine(root, "tools/tunnel-client.exe"));
Environment.SetEnvironmentVariable("AI_CONTEXT_SYNC_DATA_DIR", Path.Combine(root, ".runtime/checks"));
Check(AppPaths.ProjectRoot == root, "root override");
Check(AppPaths.TunnelClientPath == Path.GetFullPath(Path.Combine(root, "tools/tunnel-client.exe")), "client override");
Check(AppPaths.RuntimeRoot == Path.Combine(root, ".runtime/checks"), "data override");
Check(!new AppSettings().StartServiceWithTray, "startup is opt-in");
var type = typeof(TunnelController);
var valueMethod = type.GetMethod("ReadYamlValue", BindingFlags.NonPublic | BindingFlags.Static)!;
var commandMethod = type.GetMethod("ReadYamlCommand", BindingFlags.NonPublic | BindingFlags.Static)!;
foreach (var file in Directory.GetFiles(Path.Combine(root, "config/tunnel-client"), "*.example.yaml"))
{
    var yaml = File.ReadAllText(file);
    Check((string?)valueMethod.Invoke(null, [yaml, "base_url"]) == "https://api.openai.com", "base URL");
    Check((string?)valueMethod.Invoke(null, [yaml, "api_key"]) == "env:OPENAI_TUNNEL_API_KEY", "secret reference");
    Check(!string.IsNullOrEmpty((string?)commandMethod.Invoke(null, [yaml])), "quoted command");
    var def = new TunnelServiceDefinition("check", "check", file, 8081, "check");
    var method = type.GetMethod("CreateStartInfo", BindingFlags.NonPublic | BindingFlags.Static)!;
    var info = (ProcessStartInfo)method.Invoke(null, [def, "checks.log"])!;
    Check(info.ArgumentList.Contains("env:OPENAI_TUNNEL_API_KEY"), "reference passed to child");
    Check(info.ArgumentList.Contains("--mcp.command"), "stdio argument");
    Check(!info.UseShellExecute && info.CreateNoWindow, "non-shell process launch");
}
Console.WriteLine("Manager offline checks passed.");
static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"Failed: {name}");
    Console.WriteLine($"PASS {name}");
}
