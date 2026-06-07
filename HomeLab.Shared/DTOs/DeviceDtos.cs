using HomeLab.Shared.Enums;

namespace HomeLab.Shared.DTOs;

/// <summary>
/// デバイス一覧アイテム
/// </summary>
public record DeviceListItem(
    Guid Id,
    string Name,
    DeviceStatus Status,
    bool IsLocked,
    DateTimeOffset? LastSeenAt
);

/// <summary>
/// デバイス詳細
/// </summary>
public record DeviceDetail(
    Guid Id,
    string Name,
    DeviceStatus Status,
    bool IsLocked,
    string? SesameDeviceUuid,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset CreatedAt
);

/// <summary>
/// デバイス登録リクエスト (プロビジョニング時)
/// </summary>
public record DeviceRegisterRequest(
    string DevicePublicKey,
    string DeviceId,
    string SesameDeviceUuid,
    string? SesameApiKey,
    string Name
);

/// <summary>
/// デバイス登録レスポンス
/// </summary>
public record DeviceRegisterResponse(
    Guid DeviceId,
    string ActivationKey,
    string MqttBrokerUrl
);

/// <summary>
/// ロック操作リクエスト
/// </summary>
public record LockActionRequest(
    LockActionType Action
);

/// <summary>
/// ロック操作レスポンス
/// </summary>
public record LockActionResponse(
    bool Success,
    string? RequestId,
    string? ErrorMessage
);

/// <summary>
/// ロック操作ログ
/// </summary>
public record LockLogEntry(
    Guid Id,
    LockActionType ActionType,
    string? TriggeredBy,
    string? UserName,
    bool Success,
    string? ErrorMessage,
    DateTimeOffset Timestamp
);
