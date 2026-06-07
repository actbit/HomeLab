namespace HomeLab.Server.Models.Entities;

/// <summary>
/// ロック操作ログ
/// </summary>
public class LockLog
{
    public Guid Id { get; set; }

    /// <summary>
    /// 対象デバイスID
    /// </summary>
    public Guid DeviceId { get; set; }
    public LockDevice Device { get; set; } = null!;

    /// <summary>
    /// 操作の種類 (lock, unlock, toggle, status)
    /// </summary>
    public string ActionType { get; set; } = string.Empty;

    /// <summary>
    /// 操作のトリガー (user, auto, schedule)
    /// </summary>
    public string? TriggeredBy { get; set; }

    /// <summary>
    /// 操作を実行したユーザーID (null = デバイス自律動作)
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// MQTTリクエストID (トレース用)
    /// </summary>
    public string? RequestId { get; set; }

    /// <summary>
    /// 操作が成功したか
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// エラーメッセージ
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 操作日時
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
