using Microsoft.AspNetCore.SignalR;

namespace HomeLab.Server.Hubs;

/// <summary>
/// ロック状態変更のリアルタイム通知Hub
/// クライアント (Web/Native) にMQTT経由の状態変化をPush通知
/// </summary>
public class LockHub : Hub
{
    private readonly ILogger<LockHub> _logger;

    public LockHub(ILogger<LockHub> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// テナント(ユーザー)グループに参加
    /// </summary>
    public async Task JoinTenant(string tenantId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenantId}");
        _logger.LogDebug("Client {ConnectionId} joined tenant group {TenantId}",
            Context.ConnectionId, tenantId);
    }

    /// <summary>
    /// テナント(ユーザー)グループから離脱
    /// </summary>
    public async Task LeaveTenant(string tenantId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"tenant:{tenantId}");
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogDebug("LockHub client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug("LockHub client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// LockHubの通知イベント名
/// </summary>
public static class LockHubEvents
{
    /// <summary>
    /// デバイスのロック状態が変化した
    /// </summary>
    public const string LockStateChanged = "LockStateChanged";

    /// <summary>
    /// デバイスのオンライン状態が変化した
    /// </summary>
    public const string DeviceStatusChanged = "DeviceStatusChanged";

    /// <summary>
    /// 操作の結果が返ってきた
    /// </summary>
    public const string CommandResult = "CommandResult";
}
