namespace AIContextSyncTray;

internal enum ServiceState
{
    Checking,
    Connected,
    Degraded,
    Stopped
}

internal sealed record SingleServiceSnapshot(
    string Id,
    string Name,
    int Port,
    bool IsMonitored,
    ServiceState State,
    string Summary,
    string Details,
    int? TunnelProcessId,
    int? McpProcessId,
    bool LocalHealthy,
    bool LocalReady,
    bool ControlPlaneReachable,
    string? CurrentLogPath);

internal sealed record ServiceSnapshot(
    ServiceState State,
    string Summary,
    string Details,
    IReadOnlyList<SingleServiceSnapshot> Services);
