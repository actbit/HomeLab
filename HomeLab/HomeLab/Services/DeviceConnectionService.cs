using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using HomeLab.Server.Data;
using HomeLab.Server.Hubs;
using HomeLab.Shared.MQTT;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Server.Services;

/// <summary>
/// ESP32デバイスとのWebSocket接続を管理するサービス
/// MQTT不要でサーバーに内蔵されたリアルタイム通信
/// </summary>
public class DeviceConnectionService : BackgroundService
{
    private readonly ILogger<DeviceConnectionService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<LockHub> _hubContext;

    // デバイスID → WebSocket接続のマッピング
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();

    // デバイスID → ユーザーIDのマッピング
    private readonly ConcurrentDictionary<string, string> _deviceOwners = new();

    public DeviceConnectionService(
        ILogger<DeviceConnectionService> logger,
        IServiceProvider serviceProvider,
        IHubContext<LockHub> hubContext)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
    }

    /// <summary>
    /// 接続中のデバイス数
    /// </summary>
    public int ConnectedDeviceCount => _connections.Count;

    /// <summary>
    /// デバイスがオンラインかどうか
    /// </summary>
    public bool IsDeviceOnline(string deviceId)
        => _connections.ContainsKey(deviceId);

    /// <summary>
    /// ESP32からのWebSocket接続を処理
    /// </summary>
    public async Task HandleConnectionAsync(WebSocket webSocket, string deviceId, string activationKey)
    {
        // デバイス認証
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var device = await dbContext.LockDevices
            .FirstOrDefaultAsync(d => d.DeviceIdentifier == deviceId && d.ActivationKey == activationKey);

        if (device is null)
        {
            _logger.LogWarning("Device auth failed for {DeviceId}", deviceId);
            return;
        }

        // 接続登録
        _connections[device.Id.ToString()] = webSocket;
        _deviceOwners[device.Id.ToString()] = device.UserId;

        // ステータス更新: Online
        device.Status = "Online";
        device.LastSeenAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync();

        _logger.LogInformation("Device {DeviceId} connected (owner: {UserId})", device.Id, device.UserId);

        try
        {
            // メッセージ受信ループ
            var buffer = new byte[4096];

            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(buffer, CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await ProcessDeviceMessageAsync(device.Id.ToString(), device.UserId, json);
                }
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Device {DeviceId} WebSocket error", device.Id);
        }
        finally
        {
            // 接続解除
            _connections.TryRemove(device.Id.ToString(), out _);
            _deviceOwners.TryRemove(device.Id.ToString(), out _);

            // ステータス更新: Offline
            try
            {
                using var offScope = _serviceProvider.CreateScope();
                var offDb = offScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var offDevice = await offDb.LockDevices.FindAsync(device.Id);
                if (offDevice is not null)
                {
                    offDevice.Status = "Offline";
                    offDevice.LastSeenAt = DateTimeOffset.UtcNow;
                    await offDb.SaveChangesAsync();
                }
            }
            catch { /* 接続解除時のDB更新エラーは無視 */ }

            _logger.LogInformation("Device {DeviceId} disconnected", device.Id);
        }
    }

    /// <summary>
    /// デバイスにコマンドを送信 (lock/unlock/toggle)
    /// </summary>
    public async Task<bool> SendCommandAsync(Guid deviceId, string action, string requestId)
    {
        if (!_connections.TryGetValue(deviceId.ToString(), out var ws) || ws.State != WebSocketState.Open)
        {
            _logger.LogWarning("Cannot send command to offline device {DeviceId}", deviceId);
            return false;
        }

        var command = new
        {
            type = "command",
            action,
            requestId,
            timestamp = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(command);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);

        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        _logger.LogInformation("Sent command {Action} to device {DeviceId}", action, deviceId);
        return true;
    }

    /// <summary>
    /// ESP32からのメッセージを処理
    /// </summary>
    private async Task ProcessDeviceMessageAsync(string deviceId, string userId, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            switch (type)
            {
                case "heartbeat":
                    await HandleHeartbeatAsync(dbContext, deviceId, root);
                    break;

                case "status":
                    await HandleStatusAsync(dbContext, deviceId, root);
                    break;

                default:
                    _logger.LogWarning("Unknown message type '{Type}' from device {DeviceId}", type, deviceId);
                    break;
            }

            // SignalRでブラウザに通知
            await _hubContext.Clients.Group($"tenant:{userId}")
                .SendAsync(LockHubEvents.LockStateChanged, deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message from device {DeviceId}", deviceId);
        }
    }

    private async Task HandleHeartbeatAsync(AppDbContext dbContext, string deviceId, JsonElement root)
    {
        var device = await dbContext.LockDevices.FindAsync(Guid.Parse(deviceId));
        if (device is null) return;

        device.Status = "Online";
        device.IsLocked = root.TryGetProperty("isLocked", out var locked) && locked.GetBoolean();
        device.BatteryLevel = root.TryGetProperty("batteryLevel", out var bat) ? bat.GetInt32() : device.BatteryLevel;
        device.WifiRssi = root.TryGetProperty("wifiRssi", out var rssi) ? rssi.GetInt32() : device.WifiRssi;
        device.LastSeenAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync();
    }

    private async Task HandleStatusAsync(AppDbContext dbContext, string deviceId, JsonElement root)
    {
        var device = await dbContext.LockDevices.FindAsync(Guid.Parse(deviceId));
        if (device is null) return;

        device.Status = "Online";
        device.IsLocked = root.TryGetProperty("state", out var state)
            && state.GetString() == "locked";
        device.LastSeenAt = DateTimeOffset.UtcNow;

        // 操作ログ更新
        if (root.TryGetProperty("requestId", out var reqIdEl))
        {
            var requestId = reqIdEl.GetString();
            if (!string.IsNullOrEmpty(requestId))
            {
                var log = await dbContext.LockLogs
                    .FirstOrDefaultAsync(l => l.RequestId == requestId);
                if (log is not null)
                {
                    log.Success = root.TryGetProperty("success", out var suc) && suc.GetBoolean();
                    log.ErrorMessage = root.TryGetProperty("errorMessage", out var err)
                        ? err.GetString() : null;
                }
            }
        }

        await dbContext.SaveChangesAsync();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundServiceとしては何もしない (接続はHTTPミドルウェア経由で処理)
        return Task.CompletedTask;
    }
}
