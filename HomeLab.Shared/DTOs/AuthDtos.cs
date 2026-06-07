namespace HomeLab.Shared.DTOs;

/// <summary>
/// ログインリクエスト (Username + Password)
/// </summary>
public record LoginRequest(
    string Email,
    string Password,
    string? TotpCode = null
);

/// <summary>
/// ログインレスポンス
/// </summary>
public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    string TokenType,
    int ExpiresIn
);

/// <summary>
/// アカウント登録リクエスト
/// </summary>
public record RegisterRequest(
    string Email,
    string Password,
    string DisplayName
);

/// <summary>
/// パスキー登録オプションリクエスト
/// </summary>
public record PasskeyRegisterOptionsRequest(string UserId);

/// <summary>
/// パスキー登録完了リクエスト
/// </summary>
public record PasskeyRegisterCompleteRequest(
    string Id,
    string RawId,
    string Type,
    string AuthenticatorAttachment,
    string ResponseClientDataJson,
    string ResponseAttestationObject,
    string? Name
);

/// <summary>
/// パスキーログインオプションリクエスト
/// </summary>
public record PasskeyLoginOptionsRequest(string? UserId);

/// <summary>
/// パスキーログイン完了リクエスト
/// </summary>
public record PasskeyLoginCompleteRequest(
    string Id,
    string RawId,
    string Type,
    string ResponseClientDataJson,
    string ResponseAuthenticatorData,
    string ResponseSignature,
    string ResponseUserHandle
);
