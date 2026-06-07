using System.Net.Http.Json;
using HomeLab.Shared.DTOs;

namespace HomeLab.NativeClient.Services;

/// <summary>
/// サーバーAPIとの通信サービス
/// </summary>
public class ApiService
{
    private readonly HttpClient _httpClient;
    private string? _accessToken;

    public ApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public void SetAccessToken(string token)
    {
        _accessToken = token;
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public void ClearAccessToken()
    {
        _accessToken = null;
        _httpClient.DefaultRequestHeaders.Authorization = null;
    }

    // ===== 認証 =====

    public async Task<LoginResponse?> LoginAsync(LoginRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/login", request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<LoginResponse>()
            : null;
    }

    public async Task<LoginResponse?> RegisterAsync(RegisterRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/register", request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<LoginResponse>()
            : null;
    }

    // ===== デバイス =====

    public async Task<List<DeviceListItem>?> GetDevicesAsync()
    {
        return await _httpClient.GetFromJsonAsync<List<DeviceListItem>>("/api/devices");
    }

    public async Task<DeviceDetail?> GetDeviceAsync(Guid id)
    {
        return await _httpClient.GetFromJsonAsync<DeviceDetail>($"/api/devices/{id}");
    }

    public async Task<DeviceRegisterResponse?> RegisterDeviceAsync(DeviceRegisterRequest request)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/devices/register", request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<DeviceRegisterResponse>()
            : null;
    }

    public async Task<bool> DeleteDeviceAsync(Guid id)
    {
        var response = await _httpClient.DeleteAsync($"/api/devices/{id}");
        return response.IsSuccessStatusCode;
    }

    // ===== ロック操作 =====

    public async Task<LockActionResponse?> LockAsync(Guid deviceId)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/locks/{deviceId}/action",
            new LockActionRequest(Shared.Enums.LockActionType.Lock));
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<LockActionResponse>()
            : null;
    }

    public async Task<LockActionResponse?> UnlockAsync(Guid deviceId)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/locks/{deviceId}/action",
            new LockActionRequest(Shared.Enums.LockActionType.Unlock));
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<LockActionResponse>()
            : null;
    }

    public async Task<LockActionResponse?> ToggleAsync(Guid deviceId)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/locks/{deviceId}/action",
            new LockActionRequest(Shared.Enums.LockActionType.Toggle));
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<LockActionResponse>()
            : null;
    }

    // ===== ログ =====

    public async Task<List<LockLogEntry>?> GetDeviceLogsAsync(Guid deviceId, int page = 1, int pageSize = 20)
    {
        return await _httpClient.GetFromJsonAsync<List<LockLogEntry>>(
            $"/api/devices/{deviceId}/logs?page={page}&pageSize={pageSize}");
    }
}
