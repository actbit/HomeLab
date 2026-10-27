using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
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
    private const int MaxWebSocketMessageBytes = 64 * 1024;
    public const string PendingCommand = "Pending";
    public const string CommandTimedOut = "Timed out";

    private readonly ILogger<DeviceConnectionService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<LockHub> _hubContext;

    // デバイスID → WebSocket接続のマッピング
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionGates = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sendGates = new();

    // デバイスID → ユーザーIDのマッピング
    private readonly ConcurrentDictionary<string, string> _deviceOwners = new();
    private readonly ConcurrentDictionary<string, bool> _knownLockStates = new();

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

    public bool? GetKnownLockState(Guid deviceId)
        => _knownLockStates.TryGetValue(deviceId.ToString(), out var state) ? state : null;

    public static string HashActivationKey(string key)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static bool CheckActivationKey(string stored, string supplied)
    {
        var expected = stored.StartsWith("sha256:", StringComparison.Ordinal) ? HashActivationKey(supplied) : supplied;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>
    /// ESP32からのWebSocket接続を処理
    /// </summary>
    public async Task HandleConnectionAsync(WebSocket webSocket, CancellationToken cancellationToken = default)
    {
        // 最初の認証メッセージを受信
        string? authJson;
        using (var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            authTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            try { authJson = await ReceiveTextMessageAsync(webSocket, authTimeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                webSocket.Abort();
                return;
            }
        }
        if (authJson is null)
        {
            _logger.LogWarning("Invalid first message from device");
            return;
        }

        string? deviceId;
        string? activationKey;

        try
        {
            using var doc = JsonDocument.Parse(authJson);
            var root = doc.RootElement;
            deviceId = root.GetProperty("deviceId").GetString();
            activationKey = root.GetProperty("activationKey").GetString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse auth message");
            return;
        }

        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(activationKey))
        {
            _logger.LogWarning("Missing deviceId or activationKey in auth message");
            return;
        }

        // デバイス認証
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var device = await dbContext.LockDevices
            .FirstOrDefaultAsync(d => d.DeviceIdentifier == deviceId);

        if (device is null || string.IsNullOrEmpty(device.ActivationKey) || !CheckActivationKey(device.ActivationKey, activationKey))
        {
            _logger.LogWarning("Device auth failed for {DeviceId}", deviceId);
            return;
        }

        // 接続登録
        var connectionKey = device.Id.ToString();

        var connectionGate = _connectionGates.GetOrAdd(connectionKey, _ => new SemaphoreSlim(1, 1));
        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            // 同じデバイスの旧接続と状態更新を直列化する。
            if (_connections.TryGetValue(connectionKey, out var previousConnection)
                && !ReferenceEquals(previousConnection, webSocket))
                previousConnection.Abort();

            device.Status = "Online";
            if (!device.ActivationKey.StartsWith("sha256:", StringComparison.Ordinal))
                device.ActivationKey = HashActivationKey(activationKey);
            device.LastSeenAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync();
            _connections[connectionKey] = webSocket;
            _deviceOwners[connectionKey] = device.UserId;
            _knownLockStates.TryRemove(connectionKey, out _);
            await _hubContext.Clients.Group($"tenant:{device.UserId}")
                .SendAsync(LockHubEvents.DeviceStatusChanged, connectionKey);
        }
        finally { connectionGate.Release(); }

        _logger.LogInformation("Device {DeviceId} connected (owner: {UserId})", device.Id, device.UserId);

        try
        {
            // メッセージ受信ループ
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var heartbeatTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                heartbeatTimeout.CancelAfter(TimeSpan.FromSeconds(100));
                string? json;
                try
                {
                    json = await ReceiveTextMessageAsync(webSocket, heartbeatTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Device {DeviceId} heartbeat timed out", device.Id);
                    webSocket.Abort();
                    break;
                }
                if (json is null)
                {
                    if (webSocket.State == WebSocketState.CloseReceived)
                    {
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closing",
                            CancellationToken.None);
                    }
                    break;
                }

                await connectionGate.WaitAsync(cancellationToken);
                try
                {
                    if (!_connections.TryGetValue(connectionKey, out var active) || !ReferenceEquals(active, webSocket)) break;
                    await ProcessDeviceMessageAsync(connectionKey, device.UserId, json);
                }
                finally { connectionGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Device {DeviceId} connection was cancelled", device.Id);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Device {DeviceId} WebSocket error", device.Id);
        }
        finally
        {
            await connectionGate.WaitAsync();
            try
            {
                if (_connections.TryGetValue(connectionKey, out var currentConnection)
                    && ReferenceEquals(currentConnection, webSocket))
                {
                    _connections.TryRemove(connectionKey, out _);
                    _deviceOwners.TryRemove(connectionKey, out _);
                    _knownLockStates.TryRemove(connectionKey, out _);
                    using var offScope = _serviceProvider.CreateScope();
                    var offDb = offScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var offDevice = await offDb.LockDevices.FindAsync(device.Id);
                    if (offDevice is not null)
                    {
                        offDevice.Status = "Offline";
                        offDevice.LastSeenAt = DateTimeOffset.UtcNow;
                        await offDb.SaveChangesAsync();
                        await _hubContext.Clients.Group($"tenant:{device.UserId}")
                            .SendAsync(LockHubEvents.DeviceStatusChanged, connectionKey);
                    }
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "Error updating device status to offline for device {DeviceId}", device.Id); }
            finally { connectionGate.Release(); }

            _logger.LogInformation("Device {DeviceId} disconnected", device.Id);
        }
    }

    /// <summary>
    /// デバイスにコマンドを送信 (lock/unlock/toggle)
    /// </summary>
    public async Task<bool> SendCommandAsync(Guid deviceId, string action, string requestId)
    {
        var sendGate = _sendGates.GetOrAdd(deviceId.ToString(), _ => new SemaphoreSlim(1, 1));
        await sendGate.WaitAsync();
        try { return await SendCommandCoreAsync(deviceId, action, requestId); }
        finally { sendGate.Release(); }
    }

    private async Task<bool> SendCommandCoreAsync(Guid deviceId, string action, string requestId)
    {
        if (!_connections.TryGetValue(deviceId.ToString(), out var ws))
        {
            _logger.LogWarning("Device {DeviceId} not connected", deviceId);
            return false;
        }

        try
        {
            if (ws.State != WebSocketState.Open)
            {
                _logger.LogWarning("Device {DeviceId} websocket not open", deviceId);
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

            _knownLockStates.TryRemove(deviceId.ToString(), out _);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            _logger.LogInformation("Sent command {Action} to device {DeviceId}", action, deviceId);
            return true;
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket error sending to device {DeviceId}", deviceId);
            // 接続解除を試みる
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error sending to device {DeviceId}", deviceId);
            return false;
        }
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

    private static async Task<string?> ReceiveTextMessageAsync(
        WebSocket webSocket,
        CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[4096];

        while (true)
        {
            var result = await webSocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                return null;
            }

            if (message.Length + result.Count > MaxWebSocketMessageBytes)
            {
                return null;
            }

            await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
            }
        }
    }

    private async Task HandleHeartbeatAsync(AppDbContext dbContext, string deviceId, JsonElement root)
    {
        var device = await dbContext.LockDevices.FindAsync(Guid.Parse(deviceId));
        if (device is null) return;

        device.Status = "Online";
        if (root.TryGetProperty("stateConfirmed", out var confirmed) && confirmed.ValueKind == JsonValueKind.True &&
            root.TryGetProperty("isLocked", out var locked) &&
            (locked.ValueKind == JsonValueKind.True || locked.ValueKind == JsonValueKind.False))
        {
            device.IsLocked = locked.GetBoolean();
            _knownLockStates[deviceId] = device.IsLocked;
        }
        else
        {
            _knownLockStates.TryRemove(deviceId, out _);
        }
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
        var success = root.TryGetProperty("success", out var suc) && suc.ValueKind == JsonValueKind.True;
        var stateValue = root.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
            ? state.GetString() : null;
        var confirmed = root.TryGetProperty("stateConfirmed", out var confirmedValue) && confirmedValue.ValueKind == JsonValueKind.True
            && (stateValue == "locked" || stateValue == "unlocked");
        if (success && confirmed)
        {
            device.IsLocked = stateValue == "locked";
            _knownLockStates[deviceId] = device.IsLocked;
        }
        else
        {
            _knownLockStates.TryRemove(deviceId, out _);
        }
        device.LastSeenAt = DateTimeOffset.UtcNow;

        // 操作ログ更新
        if (root.TryGetProperty("requestId", out var reqIdEl))
        {
            var requestId = reqIdEl.GetString();
            if (!string.IsNullOrEmpty(requestId))
            {
                var log = await dbContext.LockLogs
                    .FirstOrDefaultAsync(l => l.DeviceId == device.Id && l.RequestId == requestId);
                if (log is not null)
                {
                    log.Success = success && confirmed;
                    log.ErrorMessage = root.TryGetProperty("errorMessage", out var err)
                        ? err.GetString() : success && !confirmed ? "State not confirmed" : null;
                }
            }
        }

        await dbContext.SaveChangesAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 接続はプロセス内で管理するため再起動後は以前のオンライン表示を無効にする。
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var previouslyOnline = await db.LockDevices.Where(d => d.Status == "Online").ToListAsync(stoppingToken);
            foreach (var device in previouslyOnline) device.Status = "Offline";
            await db.SaveChangesAsync(stoppingToken);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var pending = await db.LockLogs.Where(l => l.ErrorMessage == PendingCommand)
                    .Include(l => l.Device).ToListAsync(stoppingToken);
                foreach (var log in pending.Where(l => l.Timestamp < DateTimeOffset.UtcNow.AddSeconds(-45)))
                {
                    log.ErrorMessage = CommandTimedOut;
                }
                await db.SaveChangesAsync(stoppingToken);
                foreach (var log in pending.Where(l => l.ErrorMessage == CommandTimedOut))
                {
                    await _hubContext.Clients.Group($"tenant:{log.Device.UserId}")
                        .SendAsync(LockHubEvents.CommandResult, log.DeviceId, log.RequestId, false, CommandTimedOut, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to update timed-out device commands");
            }
        }
    }
}
