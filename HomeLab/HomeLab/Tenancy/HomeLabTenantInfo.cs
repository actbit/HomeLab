using Finbuckle.MultiTenant.Abstractions;

namespace HomeLab.Server.Tenancy;

/// <summary>
/// テナント情報 (ユーザーごとに1テナント)
/// Finbuckle v10: ITenantInfo は Finbuckle.MultiTenant.Abstractions 名前空間
/// </summary>
public class HomeLabTenantInfo : TenantInfo
{
    /// <summary>
    /// テナント専用のDB接続文字列 (マルチテナントDB分離)
    /// </summary>
    public string? ConnectionString { get; set; }
}
