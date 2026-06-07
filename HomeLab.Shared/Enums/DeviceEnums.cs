namespace HomeLab.Shared.Enums;

/// <summary>
/// ESP32デバイスの接続状態
/// </summary>
public enum DeviceStatus
{
    Online,
    Offline,
    Unknown
}

/// <summary>
/// ロック操作の種類
/// </summary>
public enum LockActionType
{
    Lock,
    Unlock,
    Toggle,
    Status
}

/// <summary>
/// 操作のトリガー
/// </summary>
public enum TriggeredBy
{
    User,
    Auto,
    Schedule
}
