namespace HomeLab.Server.Models.Entities;

/// <summary>
/// FIDO2/WebAuthn パスキー認証情報
/// </summary>
public class PasskeyCredential
{
    public Guid Id { get; set; }

    /// <summary>
    /// 紐付くユーザーID
    /// </summary>
    public string UserId { get; set; } = string.Empty;
    public AppUser User { get; set; } = null!;

    /// <summary>
    /// FIDO2 Credential ID (Base64URL)
    /// </summary>
    public byte[] CredentialId { get; set; } = [];

    /// <summary>
    /// 公開鍵 (COSE形式)
    /// </summary>
    public byte[] PublicKey { get; set; } = [];

    /// <summary>
    /// ユーザーハンドル
    /// </summary>
    public byte[] UserHandle { get; set; } = [];

    /// <summary>
    /// 署名カウンター
    /// </summary>
    public uint SignatureCounter { get; set; }

    /// <summary>
    /// 認証器の種類 (platform, cross-platform)
    /// </summary>
    public string AuthenticatorType { get; set; } = string.Empty;

    /// <summary>
    /// ユーザーが設定したデバイス名 ("iPhone", "Windows PC" 等)
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// AAGUID (Authenticator Attestation GUID)
    /// </summary>
    public byte[] Aaguid { get; set; } = [];

    /// <summary>
    /// 登録日時
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 最終使用日時
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }
}
