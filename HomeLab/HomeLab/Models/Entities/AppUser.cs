using Microsoft.AspNetCore.Identity;

namespace HomeLab.Server.Models.Entities;

/// <summary>
/// アプリケーションユーザー (ASP.NET Identity拡張)
/// 各ユーザーが1テナントとして機能
/// </summary>
public class AppUser : IdentityUser
{
    /// <summary>
    /// 表示名
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// TOTP共有秘密鍵 (Base64)
    /// </summary>
    public string? TotpSecretKey { get; set; }

    /// <summary>
    /// TOTP認証が有効かどうか
    /// </summary>
    public bool TotpEnabled { get; set; }

    /// <summary>
    /// 登録済みパスキー認証情報
    /// </summary>
    public ICollection<PasskeyCredential> PasskeyCredentials { get; set; } = [];

    /// <summary>
    /// 所有するロックデバイス
    /// </summary>
    public ICollection<LockDevice> Devices { get; set; } = [];

    /// <summary>
    /// アカウント作成日時
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
