using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HomeLab.Server.Authentication.Passkey;
using HomeLab.Server.Authentication.Totp;
using HomeLab.Server.Data;
using HomeLab.Server.Models.Entities;
using HomeLab.Shared.DTOs;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace HomeLab.Server.API;

/// <summary>
/// 認証関連のMinimal APIエンドポイント
/// </summary>
public static class AuthEndpoints
{
    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Authentication");

        // ===== Username + Password ログイン =====
        group.MapPost("/login", async (
            [FromBody] LoginRequest request,
            UserManager<AppUser> userManager,
            SignInManager<AppUser> signInManager,
            TotpService totpService,
            IConfiguration config,
            ILogger<Program> logger) =>
        {
            var user = await userManager.FindByEmailAsync(request.Email);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            // パスワード検証
            var result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
            if (!result.Succeeded)
            {
                return Results.Unauthorized();
            }

            // TOTP検証 (有効な場合)
            if (user.TotpEnabled)
            {
                if (string.IsNullOrEmpty(request.TotpCode) ||
                    !totpService.ValidateCode(user.TotpSecretKey!, request.TotpCode))
                {
                    return Results.Json(new { error = "TOTP code required or invalid" }, statusCode: 401);
                }
            }

            var token = GenerateJwtToken(user, config);
            return Results.Ok(new LoginResponse(token, "", "Bearer", 900));
        });

        // ===== アカウント登録 =====
        group.MapPost("/register", async (
            [FromBody] RegisterRequest request,
            UserManager<AppUser> userManager,
            IConfiguration config,
            ILogger<Program> logger) =>
        {
            var existingUser = await userManager.FindByEmailAsync(request.Email);
            if (existingUser is not null)
            {
                return Results.BadRequest(new { error = "Email already registered" });
            }

            var user = new AppUser
            {
                UserName = request.Email,
                Email = request.Email,
                DisplayName = request.DisplayName,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var result = await userManager.CreateAsync(user, request.Password);
            if (!result.Succeeded)
            {
                return Results.BadRequest(new { errors = result.Errors.Select(e => e.Description) });
            }

            var token = GenerateJwtToken(user, config);
            return Results.Ok(new LoginResponse(token, "", "Bearer", 900));
        });

        // ===== Passkey 登録オプション =====
        group.MapPost("/passkey/register-options", async (
            [FromBody] PasskeyRegisterOptionsRequest request,
            PasskeyService passkeyService,
            UserManager<AppUser> userManager) =>
        {
            var user = await userManager.FindByIdAsync(request.UserId);
            if (user is null)
            {
                return Results.NotFound();
            }

            var options = await passkeyService.CreateRegistrationOptionsAsync(user);
            return Results.Ok(options);
        });

        // ===== Passkey 登録完了 =====
        group.MapPost("/passkey/register", async (
            [FromBody] PasskeyRegisterCompleteRequest request,
            PasskeyService passkeyService,
            UserManager<AppUser> userManager,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return Results.Unauthorized();
            }

            var user = await userManager.FindByIdAsync(userId);
            if (user is null)
            {
                return Results.NotFound();
            }

            // 注: 実際の実装ではセッションからoptionsを復元する必要がある
            // ここでは簡略化のためスキップ

            return Results.Ok(new { success = true });
        });

        // ===== Passkey ログインオプション =====
        group.MapPost("/passkey/login-options", async (
            [FromBody] PasskeyLoginOptionsRequest request,
            PasskeyService passkeyService) =>
        {
            var options = await passkeyService.CreateLoginOptionsAsync(request.UserId);
            return Results.Ok(options);
        });

        // ===== Passkey ログイン =====
        group.MapPost("/passkey/login", async (
            [FromBody] PasskeyLoginCompleteRequest request,
            PasskeyService passkeyService,
            IConfiguration config) =>
        {
            // 注: 実際の実装では assertionResponse をデコードして検証
            var user = await passkeyService.CompleteLoginAsync(null!);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var token = GenerateJwtToken(user, config);
            return Results.Ok(new LoginResponse(token, "", "Bearer", 900));
        });

        // ===== TOTP セットアップ (QRコード生成) =====
        group.MapPost("/totp/setup", async (
            TotpService totpService,
            UserManager<AppUser> userManager,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return Results.Unauthorized();
            }

            var user = await userManager.FindByIdAsync(userId);
            if (user is null)
            {
                return Results.NotFound();
            }

            var secretKey = totpService.GenerateSecretKey();
            var qrCodeUri = totpService.GenerateQrCodeUri(user.Email!, secretKey);
            var qrCodePng = totpService.GenerateQrCodePng(qrCodeUri);

            // 秘密鍵を一時保存 (確認後に有効化)
            user.TotpSecretKey = secretKey;
            await userManager.UpdateAsync(user);

            return Results.File(qrCodePng, "image/png");
        });

        // ===== TOTP 有効化確認 =====
        group.MapPost("/totp/enable", async (
            [FromBody] TotpEnableRequest request,
            TotpService totpService,
            UserManager<AppUser> userManager,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return Results.Unauthorized();
            }

            var user = await userManager.FindByIdAsync(userId);
            if (user is null || string.IsNullOrEmpty(user.TotpSecretKey))
            {
                return Results.BadRequest(new { error = "TOTP not set up" });
            }

            if (!totpService.ValidateCode(user.TotpSecretKey, request.Code))
            {
                return Results.BadRequest(new { error = "Invalid TOTP code" });
            }

            user.TotpEnabled = true;
            await userManager.UpdateAsync(user);

            return Results.Ok(new { success = true });
        });

        // ===== TOTP 無効化 =====
        group.MapPost("/totp/disable", async (
            [FromBody] TotpDisableRequest request,
            TotpService totpService,
            UserManager<AppUser> userManager,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return Results.Unauthorized();
            }

            var user = await userManager.FindByIdAsync(userId);
            if (user is null || !user.TotpEnabled)
            {
                return Results.BadRequest(new { error = "TOTP not enabled" });
            }

            // TOTPコードまたはパスワードで確認
            if (!totpService.ValidateCode(user.TotpSecretKey!, request.Code))
            {
                return Results.BadRequest(new { error = "Invalid TOTP code" });
            }

            user.TotpEnabled = false;
            user.TotpSecretKey = null;
            await userManager.UpdateAsync(user);

            return Results.Ok(new { success = true });
        });

        // ===== パスキー一覧取得 =====
        group.MapGet("/passkeys", async (
            PasskeyService passkeyService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return Results.Unauthorized();
            }

            var passkeys = await passkeyService.GetUserPasskeysAsync(userId);
            return Results.Ok(passkeys.Select(p => new
            {
                p.Id,
                p.Name,
                p.AuthenticatorType,
                p.CreatedAt,
                p.LastUsedAt,
            }));
        });

        // ===== パスキー削除 =====
        group.MapDelete("/passkeys/{credentialId:guid}", async (
            Guid credentialId,
            PasskeyService passkeyService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return Results.Unauthorized();
            }

            await passkeyService.DeletePasskeyAsync(credentialId, userId);
            return Results.Ok(new { success = true });
        });

        return app;
    }

    /// <summary>
    /// JWTトークンを生成
    /// </summary>
    private static string GenerateJwtToken(AppUser user, IConfiguration config)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(config["Jwt:SecretKey"]!));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new(ClaimTypes.Name, user.DisplayName ?? user.Email ?? string.Empty),
            new("tenant_id", user.Id), // Finbuckle用テナントID
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var expirationMinutes = config.GetValue<int>("Jwt:AccessTokenExpirationMinutes", 15);
        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expirationMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

// TOTP用リクエストDTO (Sharedにも定義可能)
public record TotpEnableRequest(string Code);
public record TotpDisableRequest(string Code);
