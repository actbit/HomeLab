using System.Net;
using System.Net.Http.Json;
using HomeLab.Shared.DTOs;
using HomeLab.Shared.Enums;

namespace HomeLab.Server.Tests;

/// <summary>
/// ロック操作エンドポイントの統合テスト
/// </summary>
public class LockEndpointsTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _userId;
    private readonly string _token;

    public LockEndpointsTests()
    {
        _factory = new CustomWebApplicationFactory();
        _client = _factory.CreateClient();
        var email = $"lock_{Guid.NewGuid():N}@test.com";
        (_userId, _token) = _factory.CreateTestUserAsync(email).GetAwaiter().GetResult();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
    }

    [Fact]
    public async Task Unlock_OnlineDevice_ReturnsOk()
    {
        var device = await _factory.CreateTestDeviceAsync(_userId, "オンライン", "Online", isLocked: true);

        var response = await _client.PostAsJsonAsync(
            $"/api/locks/{device.Id}/action",
            new LockActionRequest(LockActionType.Unlock));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<LockActionResponse>();
        Assert.NotNull(result);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Lock_OnlineDevice_ReturnsOk()
    {
        var device = await _factory.CreateTestDeviceAsync(_userId, "オンライン", "Online");

        var response = await _client.PostAsJsonAsync(
            $"/api/locks/{device.Id}/action",
            new LockActionRequest(LockActionType.Lock));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Toggle_OnlineDevice_ReturnsOk()
    {
        var device = await _factory.CreateTestDeviceAsync(_userId, "オンライン", "Online");

        var response = await _client.PostAsJsonAsync(
            $"/api/locks/{device.Id}/action",
            new LockActionRequest(LockActionType.Toggle));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unlock_OfflineDevice_ReturnsBadRequest()
    {
        var device = await _factory.CreateTestDeviceAsync(_userId, "オフライン", "Offline");

        var response = await _client.PostAsJsonAsync(
            $"/api/locks/{device.Id}/action",
            new LockActionRequest(LockActionType.Unlock));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unlock_NonExistentDevice_ReturnsNotFound()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/locks/{Guid.NewGuid()}/action",
            new LockActionRequest(LockActionType.Unlock));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unlock_OtherUsersDevice_ReturnsNotFound()
    {
        var (otherId, _) = await _factory.CreateTestUserAsync($"other_{Guid.NewGuid():N}@test.com");
        var device = await _factory.CreateTestDeviceAsync(otherId, "他人のデバイス", "Online");

        var response = await _client.PostAsJsonAsync(
            $"/api/locks/{device.Id}/action",
            new LockActionRequest(LockActionType.Unlock));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetStatus_ReturnsOk()
    {
        var device = await _factory.CreateTestDeviceAsync(_userId, "ステータス確認", "Online");

        var response = await _client.GetAsync($"/api/locks/{device.Id}/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetStatus_NonExistent_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/locks/{Guid.NewGuid()}/status");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
