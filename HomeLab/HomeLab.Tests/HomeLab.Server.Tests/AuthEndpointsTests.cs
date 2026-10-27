using System.Net;
using System.Net.Http.Json;
using HomeLab.Server.Models.Entities;
using HomeLab.Shared.DTOs;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.WebUtilities;
using System.Text;
using System.Text.Json;
using HomeLab.Server.Authentication.Totp;
using OtpNet;

namespace HomeLab.Server.Tests;

/// <summary>
/// 認証エンドポイントの統合テスト
/// テストごとに独立したDBインスタンスを使用
/// </summary>
public class AuthEndpointsTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AuthEndpointsTests()
    {
        _factory = new CustomWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Register_ValidRequest_ReturnsOkWithToken()
    {
        var request = new RegisterRequest("register@test.com", "SecurePass123!", "テストユーザー");

        var response = await _client.PostAsJsonAsync("/api/auth/register", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.AccessToken));
        Assert.Equal("Bearer", result.TokenType);
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsBadRequest()
    {
        var email = "duplicate@test.com";
        await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(email, "Pass123!a", "ユーザー1"));
        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(email, "Pass456!b", "ユーザー2"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_WeakPassword_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("weak@test.com", "weak", "弱パスワード"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsOk()
    {
        var email = "login@test.com";
        var password = "LoginPass123!";
        await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(email, password, "ログインテスト"));

        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(email, password));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.AccessToken));
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("wrong@test.com", "CorrectPass123!", "ユーザー"));

        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("wrong@test.com", "WrongPass456!"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_NonExistentUser_ReturnsUnauthorized()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("noone@test.com", "SomePass123!"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithValidToken_ReturnsOk()
    {
        var (_, token) = await _factory.CreateTestUserAsync("protected@test.com");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/devices");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TotpStatusAndSetup_WhenEnabled_PreservesExistingSecret()
    {
        var (userId, token) = await _factory.CreateTestUserAsync("totp-enabled@test.com");
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByIdAsync(userId))!;
            user.TotpEnabled = true;
            user.TotpSecretKey = "JBSWY3DPEHPK3PXP";
            await users.UpdateAsync(user);
        }
        _client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var status = await _client.GetFromJsonAsync<TotpStatus>("/api/auth/totp/status");
        Assert.True(status!.Enabled);
        var setup = await _client.PostAsync("/api/auth/totp/setup", null);
        Assert.Equal(HttpStatusCode.BadRequest, setup.StatusCode);

        using var checkScope = _factory.Services.CreateScope();
        var checkedUser = await checkScope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(userId);
        Assert.Equal("JBSWY3DPEHPK3PXP", checkedUser!.TotpSecretKey);
    }

    [Fact]
    public async Task SvelteSettingsAndProvisioning_AreAvailableAfterLogin()
    {
        await _factory.CreateTestUserAsync("svelte@test.com");
        var login = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest("svelte@test.com", "TestPass123!"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        foreach (var route in new[] { "/settings/totp", "/settings/passkeys", "/setup/provision" })
        {
            var response = await _client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("data-svelte-root=\"App\"", html);
            Assert.Contains("/js/ble-provisioning.js", html);
            Assert.DoesNotContain("blazor.web.js", html);
        }
    }

    private sealed record TotpStatus(bool Enabled);

    [Fact]
    public async Task RecoveryCode_AuthenticatesOnce()
    {
        var (userId, _) = await _factory.CreateTestUserAsync("recover@test.com");
        string code;
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByIdAsync(userId))!;
            user.TotpEnabled = true;
            user.TotpSecretKey = "JBSWY3DPEHPK3PXP";
            await users.UpdateAsync(user);
            code = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 2))!.First();
        }

        var request = new LoginRequest("recover@test.com", "TestPass123!", code);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/auth/login", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/auth/login", request)).StatusCode);
    }

    [Fact]
    public async Task TotpSetup_ProtectsSecretAndRecoveryCodeCanDisableTotp()
    {
        var (userId, token) = await _factory.CreateTestUserAsync("totp-flow@test.com");
        _client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/api/auth/totp/setup", null)).StatusCode);
        string secret;
        using (var scope = _factory.Services.CreateScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(userId);
            Assert.StartsWith("protected:v1:", user!.TotpSecretKey);
            secret = scope.ServiceProvider.GetRequiredService<TotpSecretProtector>().Unprotect(user.TotpSecretKey)!;
        }
        var code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();
        var enabled = await _client.PostAsJsonAsync("/api/auth/totp/enable", new { code });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        using var json = JsonDocument.Parse(await enabled.Content.ReadAsStringAsync());
        var recoveryCode = json.RootElement.GetProperty("recoveryCodes")[0].GetString();
        Assert.False(string.IsNullOrWhiteSpace(recoveryCode));
        var disabled = await _client.PostAsJsonAsync("/api/auth/totp/disable", new { code = recoveryCode });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
    }

    [Fact]
    public async Task PasswordResetToken_IsSingleUse()
    {
        var (userId, _) = await _factory.CreateTestUserAsync("reset@test.com");
        string token;
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GeneratePasswordResetTokenAsync((await users.FindByIdAsync(userId))!)));
        }
        var request = new { email = "reset@test.com", token, password = "NewPassword123!" };
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/auth/reset-password", request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/auth/reset-password", request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest("reset@test.com", "NewPassword123!"))).StatusCode);
    }

    [Fact]
    public async Task PasskeyRegistration_RequiresAuthenticationAndConsumesChallengeOnFailure()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsync("/api/auth/passkey/register-options", null)).StatusCode);
        var (_, token) = await _factory.CreateTestUserAsync("passkey@test.com");
        _client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var start = await _client.PostAsync("/api/auth/passkey/register-options", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var document = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        var id = document.RootElement.GetProperty("challengeId").GetString();
        var request = new { challengeId = id, name = "test", credential = new {
            id = "AA", rawId = "AA", type = "public-key", clientExtensionResults = new { },
            response = new { clientDataJSON = "AA", attestationObject = "AA", transports = Array.Empty<string>() }
        } };
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/auth/passkey/register", request)).StatusCode);
        var replay = await _client.PostAsJsonAsync("/api/auth/passkey/register", request);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains("challenge expired", await replay.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/counter", "Current count")]
    [InlineData("/weather", "Weather")]
    public async Task FormerSamplePages_AreServedBySvelte(string route, string heading)
    {
        var response = await _client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-svelte-root=\"App\"", html);
        Assert.Contains(heading, html);
    }
}
