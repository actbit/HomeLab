using Microsoft.EntityFrameworkCore;

namespace HomeLab.Server.Data.DbProviders;

/// <summary>
/// サポートするデータベースプロバイダーの種類
/// </summary>
public enum DatabaseProvider
{
    PostgreSQL,
    SQLite
}

/// <summary>
/// データベース設定オプション
/// </summary>
public class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// 使用するプロバイダー
    /// </summary>
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.SQLite;

    /// <summary>
    /// プロバイダー別接続文字列
    /// </summary>
    public Dictionary<string, string> ConnectionString { get; set; } = new();
}

/// <summary>
/// 設定に基づいて適切なDbContextを構成するヘルパー
/// </summary>
public static class DatabaseConfiguration
{
    public static IServiceCollection AddDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var dbOptions = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);

        services.AddDbContext<AppDbContext>(options =>
        {
            var connectionString = GetConnectionString(dbOptions);

            switch (dbOptions.Provider)
            {
                case DatabaseProvider.PostgreSQL:
                    options.UseNpgsql(connectionString, npgsqlOptions =>
                    {
                        npgsqlOptions.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                    });
                    break;

                case DatabaseProvider.SQLite:
                    options.UseSqlite(connectionString, sqliteOptions =>
                    {
                        sqliteOptions.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                    });
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported database provider: {dbOptions.Provider}");
            }
        });

        return services;
    }

    public static string GetConnectionString(DatabaseOptions options)
    {
        var providerName = options.Provider.ToString();
        return options.ConnectionString.TryGetValue(providerName, out var connectionString)
            ? connectionString
            : throw new InvalidOperationException($"Connection string for '{providerName}' not found.");
    }
}
