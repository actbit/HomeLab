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
using SvelteSharp;
using SvelteSharp.AspNetCore;
using SvelteSharp.Jint;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Finbuckle.MultiTenant.Extensions;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using System.Threading.RateLimiting;

namespace HomeLab.Server;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Finbuckleのテナントストアがログイン前の画面でも解決されるため、
        // 開発・単一サーバー構成ではインメモリ分散キャッシュを必ず登録する。
        // 本番で複数台にする場合はRedis等のIDistributedCache実装へ差し替える。
        builder.Services.AddDistributedMemoryCache();

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

        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/login";
            options.LogoutPath = "/logout";
            options.Cookie.Name = "HomeLab.Auth";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.HttpOnly = true;
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                else context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                else context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
        });

        // 認証設定
        builder.Services.AddAuthentication(options =>
        {
            // ブラウザはCookie、ネイティブクライアントはBearer JWTを使う。
            options.DefaultAuthenticateScheme = "HomeLabAuth";
            options.DefaultChallengeScheme = "HomeLabAuth";
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
            .AddPolicyScheme("HomeLabAuth", "Cookie or JWT", options =>
            {
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.Authorization.ToString()
                        .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? JwtBearerDefaults.AuthenticationScheme
                        : IdentityConstants.ApplicationScheme;
            })
            .AddScheme<EspDeviceAuthOptions, EspDeviceAuthHandler>("DeviceAuth", null);

        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("authentication", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
        });

        // Passkey (FIDO2) 認証
        builder.Services.AddPasskeyAuthentication(builder.Configuration);

        // TOTP 認証
        builder.Services.AddScoped<TotpService>();
        builder.Services.AddSingleton<TotpSecretProtector>();
        builder.Services.AddSingleton<PasswordResetMailer>();

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

        var svelte = builder.Services.AddSvelteSharp(options =>
        {
            options.ViewsPath = "Views";
        });
        svelte.UseJintForCompiler()
            .UseJintForSsr();

        var app = builder.Build();

        // SQLite has EF migrations; existing EnsureCreated databases are checked before adoption.
        if (!app.Environment.IsEnvironment("Testing"))
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (db.Database.IsSqlite()) await SqliteMigrationInitializer.InitializeAsync(db);
            else if (db.Database.IsNpgsql())
            {
                // The checked-in initial EF migration is SQLite-specific.
                await db.Database.EnsureCreatedAsync();
                await TemporaryKeySchema.EnsureAsync(db);
            }
            else throw new InvalidOperationException("Unsupported database provider.");
        }

        app.MapDefaultEndpoints();

        // HTTP リクエストパイプライン
        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
        {
            app.UseExceptionHandler(handler => handler.Run(async context =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync("サーバーエラーが発生しました。");
            }));
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/reset-password" || context.Request.Path == "/temporary-key")
            {
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers.CacheControl = "no-store";
            }
            await next();
        });

        // WebSocket (ESP32デバイス接続用)
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        // Finbuckle マルチテナントミドルウェア (テスト環境ではスキップ)
        if (!app.Environment.IsEnvironment("Testing"))
        {
            app.UseMultiTenant();
        }

        // SvelteSharpの画面をSSR + hydrateする。
        app.MapSvelteSharpAssets();
        app.MapGet("/", RenderSveltePage).WithOrder(-1000);
        app.MapGet("/login", RenderSveltePage).WithOrder(-1000);
        app.MapGet("/register", RenderSveltePage).WithOrder(-1000);
        app.MapGet("/forgot-password", RenderSveltePage).WithOrder(-1000);
        app.MapGet("/reset-password", RenderSveltePage).WithOrder(-1000);
        app.MapGet("/temporary-key", RenderSveltePage).WithOrder(-1000);
        app.MapGet("/devices", RenderSveltePage).WithOrder(-1000);
        app.MapGet("/devices/{id:guid}", RenderSveltePage).WithOrder(-1000);
        app.MapGet("/setup/provision", RenderSveltePage).WithOrder(-1000);
        app.MapGet("/settings/totp", RenderSveltePage).WithOrder(-1000);
        app.MapGet("/settings/passkeys", RenderSveltePage).WithOrder(-1000);
        app.MapGet("/counter", RenderSveltePage).WithOrder(-1000);
        app.MapGet("/weather", RenderSveltePage).WithOrder(-1000);

        // SignalR Hub
        app.MapHub<LockHub>("/hubs/lock").RequireAuthorization();
        app.MapHub<TemporaryKeyHub>("/hubs/temporary-key").RequireRateLimiting("authentication");

        // ESP32 WebSocket エンドポイント
        app.MapDeviceWebSocket();

        // Minimal API エンドポイント
        app.MapAuthEndpoints();
        app.MapDeviceEndpoints();
        app.MapLockEndpoints();
        app.MapTemporaryKeyEndpoints();

        app.Run();
    }

    private static IResult RenderSveltePage(HttpContext context)
        => Svelte.View(
            "App",
            new SveltePageModel(
                context.User.Identity?.IsAuthenticated == true,
                context.User.Identity?.Name,
                context.Request.Path.Value ?? "/"),
            SvelteRenderMode.Hybrid);
}
