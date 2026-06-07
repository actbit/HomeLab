namespace HomeLab.Shared.MQTT;

/// <summary>
/// MQTTコマンドメッセージ (サーバー → ESP32)
/// </summary>
public record MqttCommandMessage(
    string Action,
    string RequestId,
    DateTimeOffset Timestamp
);

/// <summary>
/// MQTTステータスメッセージ (ESP32 → サーバー)
/// </summary>
public record MqttStatusMessage(
    string State,
    string RequestId,
    DateTimeOffset Timestamp,
    bool? Success = null,
    string? ErrorMessage = null
);

/// <summary>
/// MQTTハートビートメッセージ (ESP32 → サーバー)
/// </summary>
public record MqttHeartbeatMessage(
    string DeviceId,
    bool IsLocked,
    int BatteryLevel,
    int WifiRssi,
    DateTimeOffset Timestamp
);
