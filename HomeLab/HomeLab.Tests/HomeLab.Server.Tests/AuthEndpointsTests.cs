using System.Net;
using System.Net.Http.Json;
using HomeLab.Shared.DTOs;

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
}
