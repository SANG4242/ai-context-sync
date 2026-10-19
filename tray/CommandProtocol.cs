using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIContextSyncTray;

internal static class CommandProtocol
{
    public const string PipeName = "AIContextSyncTray.Control.v1";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

internal sealed record TrayCommandRequest(string Action, string Target = "all");

internal sealed record TrayCommandResponse(
    bool Success,
    string Message,
    ServiceSnapshotDto? Snapshot = null);

internal sealed record ServiceSnapshotDto(
    string State,
    string Summary,
    string Details,
    IReadOnlyList<SingleServiceSnapshotDto> Services);

internal sealed record SingleServiceSnapshotDto(
    string Id,
    string Name,
    int Port,
    bool IsMonitored,
    string State,
    string Summary,
    string Details,
    int? TunnelProcessId,
    int? McpProcessId,
    bool LocalHealthy,
    bool LocalReady,
    bool ControlPlaneReachable,
    string? CurrentLogPath);
