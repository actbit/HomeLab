namespace HomeLab.Shared.MQTT;

/// <summary>
/// MQTTトピックの命名規則を定義
/// </summary>
public static class MqttTopics
{
    /// <summary>
    /// トピックのベースプレフィックス
    /// </summary>
    public const string Prefix = "homelab";

    /// <summary>
    /// コマンドトピック: homelab/{tenantId}/{deviceId}/command
    /// サーバー → ESP32
    /// </summary>
    public static string Command(string tenantId, string deviceId)
        => $"{Prefix}/{tenantId}/{deviceId}/command";

    /// <summary>
    /// ステータストピック: homelab/{tenantId}/{deviceId}/status
    /// ESP32 → サーバー
    /// </summary>
    public static string Status(string tenantId, string deviceId)
        => $"{Prefix}/{tenantId}/{deviceId}/status";

    /// <summary>
    /// ハートビートトピック: homelab/{tenantId}/{deviceId}/heartbeat
    /// ESP32 → サーバー (定期生存確認)
    /// </summary>
    public static string Heartbeat(string tenantId, string deviceId)
        => $"{Prefix}/{tenantId}/{deviceId}/heartbeat";

    /// <summary>
    /// テナント配下全デバイスのワイルドカード
    /// </summary>
    public static string TenantWildcard(string tenantId)
        => $"{Prefix}/{tenantId}/#";
}
