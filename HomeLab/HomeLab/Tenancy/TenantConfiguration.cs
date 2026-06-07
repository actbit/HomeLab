using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Extensions;
using Finbuckle.MultiTenant.AspNetCore.Extensions;

namespace HomeLab.Server.Tenancy;

/// <summary>
/// マルチテナンシー設定
/// ユーザーごとに1テナントを割り当て、データを分離する
/// </summary>
public static class TenantConfiguration
{
    public static IServiceCollection AddMultiTenancy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMultiTenant<HomeLabTenantInfo>()
            .WithDistributedCacheStore(TimeSpan.FromDays(30))
            .WithClaimStrategy("tenant_id");

        return services;
    }
}
