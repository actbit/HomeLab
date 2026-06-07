using System.Net;
using System.Net.Http.Json;
using HomeLab.Shared.DTOs;

namespace HomeLab.Server.Tests;

/// <summary>
/// デバイスエンドポイントの統合テスト
/// 各テストが独立したDBを持つ
/// </summary>
public class DeviceEndpointsTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _token;
    private readonly string _userId;

    public DeviceEndpointsTests()
    {
        _factory = new CustomWebApplicationFactory();
        _client = _factory.CreateClient();
        // テストごとにユニークなメールアドレス
        var email = $"device_{Guid.NewGuid():N}@test.com";
        (_userId, _token) = _factory.CreateTestUserAsync(email).GetAwaiter().GetResult();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
    }

    [Fact]
    public async Task GetDevices_Empty_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var devices = await response.Content.ReadFromJsonAsync<List<DeviceListItem>>();
        Assert.NotNull(devices);
        Assert.Empty(devices);
    }

    [Fact]
    public async Task RegisterDevice_ReturnsOkWithActivationKey()
    {
        var request = new DeviceRegisterRequest(
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)),
            $"esp-{Guid.NewGuid():N}"[..16],
            Guid.NewGuid().ToString(),
            "test-key",
            "玄関SESAME");

        var response = await _client.PostAsJsonAsync("/api/devices/register", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<DeviceRegisterResponse>();
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.DeviceId);
        Assert.False(string.IsNullOrEmpty(result.ActivationKey));
    }

    [Fact]
    public async Task GetDevice_OwnDevice_ReturnsOk()
    {
        var device = await _factory.CreateTestDeviceAsync(_userId, "マイデバイス");

        var response = await _client.GetAsync($"/api/devices/{device.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<DeviceDetail>();
        Assert.NotNull(result);
        Assert.Equal("マイデバイス", result.Name);
    }

    [Fact]
    public async Task GetDevice_OtherUsersDevice_ReturnsNotFound()
    {
        // 別ユーザーのデバイス
        var (otherId, _) = await _factory.CreateTestUserAsync($"other_{Guid.NewGuid():N}@test.com");
        var device = await _factory.CreateTestDeviceAsync(otherId, "他人のデバイス");

        var response = await _client.GetAsync($"/api/devices/{device.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteDevice_ReturnsOk()
    {
        var device = await _factory.CreateTestDeviceAsync(_userId);

        var response = await _client.DeleteAsync($"/api/devices/{device.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetDeviceLogs_ReturnsOk()
    {
        var device = await _factory.CreateTestDeviceAsync(_userId);

        var response = await _client.GetAsync($"/api/devices/{device.Id}/logs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetDevices_AfterAddingTwo_ReturnsTwo()
    {
        await _factory.CreateTestDeviceAsync(_userId, "デバイスA");
        await _factory.CreateTestDeviceAsync(_userId, "デバイスB");

        var response = await _client.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var devices = await response.Content.ReadFromJsonAsync<List<DeviceListItem>>();
        Assert.NotNull(devices);
        Assert.Equal(2, devices.Count);
    }
}
