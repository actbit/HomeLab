namespace HomeLab.Server.Models.Entities;

/// <summary>
/// ESP32 ロックデバイス
/// </summary>
public class LockDevice
{
    public Guid Id { get; set; }

    /// <summary>
    /// 所有ユーザーID (テナントIDと同じ)
    /// </summary>
    public string UserId { get; set; } = string.Empty;
    public AppUser User { get; set; } = null!;

    /// <summary>
    /// デバイスの表示名 ("玄関", "勝手口" 等)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// ESP32のECDSA P-256公開鍵 (Base64)
    /// </summary>
    public string DevicePublicKey { get; set; } = string.Empty;

    /// <summary>
    /// ESP32固有のデバイス識別子
    /// </summary>
    public string DeviceIdentifier { get; set; } = string.Empty;

    /// <summary>
    /// サーバー発行のアクティベーションキー
    /// </summary>
    public string? ActivationKey { get; set; }

    /// <summary>
    /// SESAME ロックのBLE デバイスUUID
    /// </summary>
    public string SesameDeviceUuid { get; set; } = string.Empty;

    /// <summary>
    /// SESAME API有効化キー
    /// </summary>
    public string? SesameApiKey { get; set; }

    /// <summary>
    /// デバイス接続状態
    /// </summary>
    public string Status { get; set; } = "Unknown";

    /// <summary>
    /// 現在のロック状態
    /// </summary>
    public bool IsLocked { get; set; } = true;

    /// <summary>
    /// バッテリーレベル (%)
    /// </summary>
    public int? BatteryLevel { get; set; }

    /// <summary>
    /// WiFi RSSI
    /// </summary>
    public int? WifiRssi { get; set; }

    /// <summary>
    /// 最終通信日時
    /// </summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>
    /// 登録日時
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 操作ログ
    /// </summary>
    public ICollection<LockLog> Logs { get; set; } = [];
}
