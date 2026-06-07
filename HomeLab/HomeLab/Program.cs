using HomeLab.Server.API;
using HomeLab.Server.Authentication;
using HomeLab.Server.Authentication.Passkey;
using HomeLab.Server.Authentication.Totp;
using HomeLab.Server.Data;
using HomeLab.Server.Data.DbProviders;
using HomeLab.Server.Hubs;
using HomeLab.Server.Middleware;
using HomeLab.Server.Models.Entities;
using HomeLab.Server.Services;
using HomeLab.Server.Tenancy;
using HomeLab.Client.Pages;
using HomeLab.Components;
using Microsoft.AspNetCore.Identity;
using Finbuckle.MultiTenant.Extensions;
using Finbuckle.MultiTenant.AspNetCore.Extensions;

namespace HomeLab.Server;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Aspire サービスデフォルト
        builder.AddServiceDefaults();

        // データベース設定
        builder.Services.AddDatabase(builder.Configuration);

        // ASP.NET Core Identity
        builder.Services.AddIdentity<AppUser, IdentityRole>(options =>
        {
            options.Password.RequiredLength = 8;
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.User.RequireUniqueEmail = true;
            options.SignIn.RequireConfirmedAccount = false;
        })
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

        // 認証設定
        builder.Services.AddAuthentication(options =>
        {
            // APIエンドポイントではJWT、それ以外はCookie
            options.DefaultAuthenticateScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
        })
            .AddCookie(options =>
            {
                options.LoginPath = "/login";
                options.LogoutPath = "/logout";
            })
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = builder.Configuration["Jwt:Issuer"],
                    ValidAudience = builder.Configuration["Jwt:Audience"],
                    IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                        System.Text.Encoding.UTF8.GetBytes(
                            builder.Configuration["Jwt:SecretKey"]
                            ?? throw new InvalidOperationException("Jwt:SecretKey is not configured")))
                };
            })
            .AddScheme<EspDeviceAuthOptions, EspDeviceAuthHandler>("DeviceAuth", null);

        builder.Services.AddAuthorization();

        // Passkey (FIDO2) 認証
        builder.Services.AddPasskeyAuthentication(builder.Configuration);

        // TOTP 認証
        builder.Services.AddScoped<TotpService>();

        // マルチテナンシー (Finbuckle)
        builder.Services.AddMultiTenancy(builder.Configuration);

        // ESP32 WebSocket接続管理 (MQTT不要・サーバー内蔵)
        builder.Services.AddSingleton<DeviceConnectionService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DeviceConnectionService>());

        // SignalR (リアルタイム通知)
        builder.Services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = builder.Environment.IsDevelopment();
        });

        // Blazor UI
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddInteractiveWebAssemblyComponents();

        var app = builder.Build();

        // データベースマイグレーション (開発環境)
        if (app.Environment.IsDevelopment())
        {
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();
            }
        }

        app.MapDefaultEndpoints();

        // HTTP リクエストパイプライン
        if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
        {
            app.UseWebAssemblyDebugging();
        }
        else
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();

        // WebSocket (ESP32デバイス接続用)
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });

        // テスト環境ではAntiforgeryを無効化 (APIテスト用)
        if (!app.Environment.IsEnvironment("Testing"))
        {
            app.UseAntiforgery();
        }

        app.UseAuthentication();
        app.UseAuthorization();

        // Finbuckle マルチテナントミドルウェア (テスト環境ではスキップ)
        if (!app.Environment.IsEnvironment("Testing"))
        {
            app.UseMultiTenant();
        }

        // Blazor コンポーネント
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .AddInteractiveWebAssemblyRenderMode()
            .AddAdditionalAssemblies(typeof(Client._Imports).Assembly);

        // SignalR Hub
        app.MapHub<LockHub>("/hubs/lock");

        // ESP32 WebSocket エンドポイント
        app.MapDeviceWebSocket();

        // Minimal API エンドポイント
        app.MapAuthEndpoints();
        app.MapDeviceEndpoints();
        app.MapLockEndpoints();

        app.Run();
    }
}
