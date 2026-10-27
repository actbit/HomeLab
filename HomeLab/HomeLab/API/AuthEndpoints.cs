using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using HomeLab.Server.Authentication;
using HomeLab.Server.Authentication.Passkey;
using HomeLab.Server.Authentication.Totp;
using HomeLab.Server.Data;
using HomeLab.Server.Models.Entities;
using HomeLab.Shared.DTOs;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.WebUtilities;

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
            TotpSecretProtector secrets,
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
                var secret = secrets.Unprotect(user.TotpSecretKey);
                if (string.IsNullOrWhiteSpace(request.TotpCode) ||
                    ((secret is null || !totpService.ValidateCode(secret, request.TotpCode)) &&
                     !(await userManager.RedeemTwoFactorRecoveryCodeAsync(user, request.TotpCode.Trim())).Succeeded))
                {
                    return Results.Json(new { error = "TOTP code required or invalid" }, statusCode: 401);
                }
                if (secret is not null && !secrets.IsProtected(user.TotpSecretKey))
                {
                    user.TotpSecretKey = secrets.Protect(secret);
                    await userManager.UpdateAsync(user);
                }
            }

            // ブラウザUI用のCookieも発行する。
            await signInManager.SignInAsync(user, isPersistent: true);

            var token = GenerateJwtToken(user, config);
            return Results.Ok(new LoginResponse(token, "", "Bearer", 900));
        }).RequireRateLimiting("authentication");

        group.MapPost("/logout", async (HttpContext httpContext) =>
        {
            await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Results.Ok(new { success = true });
        });

        group.MapPost("/forgot-password", async ([FromBody] ForgotPasswordRequest request,
            UserManager<AppUser> users, PasswordResetMailer mailer, ILogger<Program> logger) =>
        {
            if (!mailer.IsConfigured) return Results.Problem("Password reset email is not configured", statusCode: 503);
            var user = await users.FindByEmailAsync(request.Email);
            if (user is not null && user.Email is not null)
            {
                try
                {
                    var token = await users.GeneratePasswordResetTokenAsync(user);
                    await mailer.SendAsync(user.Email, WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token)));
                }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to send password reset email"); }
            }
            return Results.Ok(new { success = true });
        }).RequireRateLimiting("authentication");

        group.MapPost("/reset-password", async ([FromBody] ResetPasswordRequest request, UserManager<AppUser> users) =>
        {
            var user = await users.FindByEmailAsync(request.Email);
            if (user is null || string.IsNullOrWhiteSpace(request.Token))
                return Results.BadRequest(new { error = "Invalid or expired reset link" });
            try
            {
                var token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(request.Token));
                var result = await users.ResetPasswordAsync(user, token, request.Password);
                return result.Succeeded ? Results.Ok(new { success = true })
                    : Results.BadRequest(new { error = "Invalid link or password does not meet the requirements" });
            }
            catch (FormatException) { return Results.BadRequest(new { error = "Invalid or expired reset link" }); }
        }).RequireRateLimiting("authentication");

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
            PasskeyService passkeyService,
            PasskeyChallengeStore challenges,
            UserManager<AppUser> userManager,
            HttpContext httpContext) =>
        {
            var user = await userManager.GetUserAsync(httpContext.User);
            if (user is null)
            {
                return Results.NotFound();
            }

            var options = await passkeyService.CreateRegistrationOptionsAsync(user);
            return Results.Ok(new { challengeId = challenges.Create(options, user.Id), options = JsonSerializer.Deserialize<JsonElement>(options.ToJson()) });
        }).RequireAuthorization();

        // ===== Passkey 登録完了 =====
        group.MapPost("/passkey/register", async (
            [FromBody] PasskeyRegistrationRequest request,
            PasskeyService passkeyService,
            PasskeyChallengeStore challenges,
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

            var options = challenges.Consume<CredentialCreateOptions>(request.ChallengeId, userId);
            if (options is null || request.Credential is null) return Results.BadRequest(new { error = "Registration challenge expired" });
            try
            {
                await passkeyService.CompleteRegistrationAsync(user, request.Credential, options, request.Name);
                return Results.Ok(new { success = true });
            }
            catch (Exception)
            {
                return Results.BadRequest(new { error = "Passkey registration verification failed" });
            }
        }).RequireAuthorization();

        // ===== Passkey ログインオプション =====
        group.MapPost("/passkey/login-options", async (
            PasskeyService passkeyService,
            PasskeyChallengeStore challenges) =>
        {
            var options = await passkeyService.CreateLoginOptionsAsync();
            return Results.Ok(new { challengeId = challenges.Create(options, null), options = JsonSerializer.Deserialize<JsonElement>(options.ToJson()) });
        }).RequireRateLimiting("authentication");

        // ===== Passkey ログイン =====
        group.MapPost("/passkey/login", async (
            [FromBody] PasskeyAssertionRequest request,
            PasskeyService passkeyService,
            PasskeyChallengeStore challenges,
            SignInManager<AppUser> signInManager,
            IConfiguration config) =>
        {
            var options = challenges.Consume<AssertionOptions>(request.ChallengeId, null);
            if (options is null || request.Credential is null) return Results.Unauthorized();
            try
            {
                var user = await passkeyService.CompleteLoginAsync(request.Credential, options);
                if (user is null || await signInManager.UserManager.IsLockedOutAsync(user)) return Results.Unauthorized();
                await signInManager.SignInAsync(user, isPersistent: true);
                return Results.Ok(new LoginResponse(GenerateJwtToken(user, config), "", "Bearer", 900));
            }
            catch (Exception)
            {
                return Results.Unauthorized();
            }
        }).RequireRateLimiting("authentication");

        group.MapGet("/totp/status", async (UserManager<AppUser> userManager, HttpContext context) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            return user is null ? Results.Unauthorized() : Results.Ok(new { enabled = user.TotpEnabled });
        }).RequireAuthorization();

        // ===== TOTP セットアップ (QRコード生成) =====
        group.MapPost("/totp/setup", async (
            TotpService totpService,
            TotpSecretProtector secrets,
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

            if (user.TotpEnabled)
            {
                return Results.BadRequest(new { error = "TOTP is already enabled" });
            }

            var secretKey = totpService.GenerateSecretKey();
            var qrCodeUri = totpService.GenerateQrCodeUri(user.Email!, secretKey);
            var qrCodePng = totpService.GenerateQrCodePng(qrCodeUri);

            // 秘密鍵を一時保存 (確認後に有効化)
            user.TotpSecretKey = secrets.Protect(secretKey);
            await userManager.UpdateAsync(user);

            return Results.File(qrCodePng, "image/png");
        });

        // ===== TOTP 有効化確認 =====
        group.MapPost("/totp/enable", async (
            [FromBody] TotpEnableRequest request,
            TotpService totpService,
            TotpSecretProtector secrets,
            UserManager<AppUser> userManager,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return Results.Unauthorized();
            }

            var user = await userManager.FindByIdAsync(userId);
            if (user is null || user.TotpEnabled || string.IsNullOrEmpty(user.TotpSecretKey))
            {
                return Results.BadRequest(new { error = "TOTP not set up" });
            }

            var secret = secrets.Unprotect(user.TotpSecretKey);
            if (secret is null || !totpService.ValidateCode(secret, request.Code))
            {
                return Results.BadRequest(new { error = "Invalid TOTP code" });
            }

            user.TotpEnabled = true;
            var update = await userManager.UpdateAsync(user);
            if (!update.Succeeded) return Results.Problem("Failed to enable TOTP");
            var recoveryCodes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 8);

            return Results.Ok(new { success = true, recoveryCodes });
        }).RequireRateLimiting("authentication");

        // ===== TOTP 無効化 =====
        group.MapPost("/totp/disable", async (
            [FromBody] TotpDisableRequest request,
            TotpService totpService,
            TotpSecretProtector secrets,
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

            // 認証器を紛失した場合は未使用のリカバリーコードも使える。
            var secret = secrets.Unprotect(user.TotpSecretKey);
            if (string.IsNullOrWhiteSpace(request.Code) ||
                ((secret is null || !totpService.ValidateCode(secret, request.Code)) &&
                 !(await userManager.RedeemTwoFactorRecoveryCodeAsync(user, request.Code.Trim())).Succeeded))
            {
                return Results.BadRequest(new { error = "Invalid TOTP code" });
            }

            user.TotpEnabled = false;
            user.TotpSecretKey = null;
            await userManager.UpdateAsync(user);

            return Results.Ok(new { success = true });
        }).RequireRateLimiting("authentication");

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
public record PasskeyRegistrationRequest(string ChallengeId, AuthenticatorAttestationRawResponse Credential, string? Name);
public record PasskeyAssertionRequest(string ChallengeId, AuthenticatorAssertionRawResponse Credential);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Email, string Token, string Password);
