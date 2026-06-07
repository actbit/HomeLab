using HomeLab.Server.Data;
using HomeLab.Server.Models.Entities;
using HomeLab.Server.Services;
using HomeLab.Shared.MQTT;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace HomeLab.Server.Tests;

/// <summary>
/// テスト用のWebApplicationFactory
/// 各インスタンスが独立したInMemoryDBを持つ
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestJwtSecretKey = "TestSecretKey_ForIntegrationTests_Minimum32Chars!";
    public readonly string DbName = $"TestDb_{Guid.NewGuid():N}";

    /// <summary>
    /// テストユーザー作成
    /// </summary>
    public async Task<(string UserId, string JwtToken)> CreateTestUserAsync(
        string email, string password = "TestPass123!", string displayName = "Test User")
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 既存チェック
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            return (existing.Id, GenerateTestJwt(existing));
        }

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"テストユーザー作成失敗: {string.Join(", ", result.Errors.Select(e => e.Description))}");
        }

        return (user.Id, GenerateTestJwt(user));
    }

    /// <summary>
    /// テスト用デバイス作成
    /// </summary>
    public async Task<LockDevice> CreateTestDeviceAsync(
        string userId, string name = "テストデバイス", string status = "Online", bool isLocked = true)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var device = new LockDevice
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            DevicePublicKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            DeviceIdentifier = Guid.NewGuid().ToString("N")[..16],
            SesameDeviceUuid = Guid.NewGuid().ToString(),
            Status = status,
            IsLocked = isLocked,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.LockDevices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    /// <summary>
    /// JWT生成
    /// </summary>
    public static string GenerateTestJwt(AppUser user)
    {
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes(TestJwtSecretKey));
        var credentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id),
            new(System.Security.Claims.ClaimTypes.Email, user.Email ?? string.Empty),
            new(System.Security.Claims.ClaimTypes.Name, user.DisplayName ?? user.Email ?? string.Empty),
            new("tenant_id", user.Id),
            new(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: "HomeLab", audience: "HomeLab",
            claims: claims, expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            var internalSp = new ServiceCollection()
                .AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();

            services.RemoveAll(typeof(DbContextOptions));
            services.RemoveAll(typeof(DbContextOptions<AppDbContext>));
            services.RemoveAll<AppDbContext>();

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseInMemoryDatabase(DbName);
                options.UseInternalServiceProvider(internalSp);
            });

            services.AddDistributedMemoryCache();

            services.RemoveAll<MqttService>();
            services.RemoveAll<IHostedService>();
            services.AddSingleton<MqttService>(_ => new MockMqttService());
        });

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "SQLite",
                ["Database:ConnectionString:SQLite"] = "Data Source=:memory:",
                ["Jwt:Issuer"] = "HomeLab",
                ["Jwt:Audience"] = "HomeLab",
                ["Jwt:SecretKey"] = TestJwtSecretKey,
                ["Jwt:AccessTokenExpirationMinutes"] = "60",
                ["Passkey:Origin"] = "https://localhost",
                ["Passkey:RelyingPartyName"] = "HomeLab Test",
                ["Passkey:RelyingPartyId"] = "localhost",
                ["Mqtt:Host"] = "localhost",
                ["Mqtt:Port"] = "1883",
            });
        });
    }
}

/// <summary>
/// テスト用モックMQTTサービス
/// </summary>
public class MockMqttService : MqttService
{
    public List<(string Topic, MqttCommandMessage Message)> PublishedCommands { get; } = [];

    public MockMqttService()
        : base(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MqttService>.Instance,
            new FakeServiceProvider(),
            new ConfigurationBuilder().Build()) { }

    public new Task PublishCommandAsync(string topic, MqttCommandMessage command)
    {
        PublishedCommands.Add((topic, command));
        return Task.CompletedTask;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}

file class FakeServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
