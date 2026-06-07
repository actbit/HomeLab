using System.Text;
using System.Text.Json;
using HomeLab.Shared.MQTT;
using Microsoft.EntityFrameworkCore;
using HomeLab.Server.Data;
using HomeLab.Server.Models.Entities;
using MQTTnet;
using MQTTnet.Protocol;

namespace HomeLab.Server.Services;

/// <summary>
/// MQTTサービス - MQTTnet組み込みブローカー + メッセージ処理
/// TODO: MQTTnet v5.1 API の正式な Server/Broker 統合を実装
/// 現在はクライアントとして動作（外部MQTTブローカー接続を想定）
/// </summary>
public class MqttService : BackgroundService
{
    private readonly ILogger<MqttService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private IMqttClient? _mqttClient;

    public MqttService(
        ILogger<MqttService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mqttHost = _configuration.GetValue<string>("Mqtt:Host") ?? "localhost";
        var mqttPort = _configuration.GetValue<int>("Mqtt:Port", 1883);

        // MQTTnet v5.1: クライアントファクトリ
        var factory = new MqttClientFactory();
        _mqttClient = factory.CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(mqttHost, mqttPort)
            .WithClientId("HomeLab.Server.Internal")
            .Build();

        // メッセージ受信ハンドラ
        _mqttClient.ApplicationMessageReceivedAsync += async args =>
        {
            var topic = args.ApplicationMessage.Topic;
            var payload = args.ApplicationMessage.ConvertPayloadToString();
            _logger.LogDebug("MQTT message received on {Topic}: {Payload}", topic, payload);

            try
            {
                var parts = topic.Split('/');
                if (parts.Length == 4 && parts[0] == MqttTopics.Prefix)
                {
                    var tenantId = parts[1];
                    var deviceId = parts[2];
                    var messageType = parts[3];

                    switch (messageType)
                    {
                        case "status":
                            await HandleStatusMessageAsync(tenantId, deviceId, payload);
                            break;
                        case "heartbeat":
                            await HandleHeartbeatMessageAsync(tenantId, deviceId, payload);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing MQTT message on topic {Topic}", topic);
            }
        };

        // 接続 (リトライ付き)
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Connecting to MQTT broker at {Host}:{Port}...", mqttHost, mqttPort);
                await _mqttClient.ConnectAsync(options, stoppingToken);
                _logger.LogInformation("Connected to MQTT broker");

                // 全デバイスのワイルドカード購読
                await _mqttClient.SubscribeAsync("homelab/+/+/status", MqttQualityOfServiceLevel.AtLeastOnce, stoppingToken);
                await _mqttClient.SubscribeAsync("homelab/+/+/heartbeat", MqttQualityOfServiceLevel.AtLeastOnce, stoppingToken);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT connection failed, retrying in 5s...");
                await Task.Delay(5000, stoppingToken);
            }
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }

        if (_mqttClient.IsConnected)
        {
            await _mqttClient.DisconnectAsync();
        }
    }

    /// <summary>
    /// MQTTコマンドをパブリッシュ (サーバー → ESP32)
    /// </summary>
    public async Task PublishCommandAsync(string topic, MqttCommandMessage command)
    {
        if (_mqttClient is null || !_mqttClient.IsConnected)
        {
            _logger.LogWarning("MQTT client not connected, cannot publish to {Topic}", topic);
            return;
        }

        var json = JsonSerializer.Serialize(command);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        await _mqttClient.PublishAsync(message);
        _logger.LogInformation("Published command to {Topic}: {Action}", topic, command.Action);
    }

    private async Task HandleStatusMessageAsync(string tenantId, string deviceId, string payload)
    {
        var status = JsonSerializer.Deserialize<MqttStatusMessage>(payload);
        if (status is null) return;

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var device = await dbContext.LockDevices
            .FirstOrDefaultAsync(d => d.Id.ToString() == deviceId && d.UserId == tenantId);
        if (device is null) return;

        device.IsLocked = status.State.Equals("locked", StringComparison.OrdinalIgnoreCase);
        device.Status = "Online";
        device.LastSeenAt = DateTimeOffset.UtcNow;

        if (!string.IsNullOrEmpty(status.RequestId))
        {
            var log = await dbContext.LockLogs.FirstOrDefaultAsync(l => l.RequestId == status.RequestId);
            if (log is not null)
            {
                log.Success = status.Success ?? true;
                log.ErrorMessage = status.ErrorMessage;
            }
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task HandleHeartbeatMessageAsync(string tenantId, string deviceId, string payload)
    {
        var heartbeat = JsonSerializer.Deserialize<MqttHeartbeatMessage>(payload);
        if (heartbeat is null) return;

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var device = await dbContext.LockDevices
            .FirstOrDefaultAsync(d => d.Id.ToString() == deviceId && d.UserId == tenantId);
        if (device is null) return;

        device.Status = "Online";
        device.IsLocked = heartbeat.IsLocked;
        device.BatteryLevel = heartbeat.BatteryLevel;
        device.WifiRssi = heartbeat.WifiRssi;
        device.LastSeenAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync();
    }
}
