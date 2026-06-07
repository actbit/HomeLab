using System.Text.Json;
using System.Net.Http.Json;
using HomeLab.NativeClient.Services;

namespace HomeLab.NativeClient.Services;

/// <summary>
/// 認証サービス (Passkey + Password + TOTP)
/// </summary>
public class AuthService
{
    private readonly ApiService _apiService;
    private readonly ILogger<AuthService> _logger;

    public AuthService(ApiService apiService, ILogger<AuthService> logger)
    {
        _apiService = apiService;
        _logger = logger;
    }

    /// <summary>
    /// パスワードでログイン
    /// </summary>
    public async Task<bool> LoginWithPasswordAsync(string email, string password, string? totpCode = null)
    {
        try
        {
            var response = await _apiService.LoginAsync(
                new Shared.DTOs.LoginRequest(email, password, totpCode));

            if (response is not null)
            {
                _apiService.SetAccessToken(response.AccessToken);
                _logger.LogInformation("Login successful for {Email}", email);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed for {Email}", email);
            return false;
        }
    }

    /// <summary>
    /// アカウント登録
    /// </summary>
    public async Task<bool> RegisterAsync(string email, string password, string displayName)
    {
        try
        {
            var response = await _apiService.RegisterAsync(
                new Shared.DTOs.RegisterRequest(email, password, displayName));

            if (response is not null)
            {
                _apiService.SetAccessToken(response.AccessToken);
                _logger.LogInformation("Registration successful for {Email}", email);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration failed for {Email}", email);
            return false;
        }
    }

    /// <summary>
    /// ログアウト
    /// </summary>
    public void Logout()
    {
        _apiService.ClearAccessToken();
    }

    /// <summary>
    /// Passkeyログイン (ネイティブプラットフォームAPIを使用)
    /// 注: 実際の実装はプラットフォームごとに異なる
    /// - Android: FIDO2 API via platform invoke
    /// - Windows: WebAuthn Win32 API via platform invoke
    /// </summary>
    public async Task<bool> LoginWithPasskeyAsync()
    {
        // TODO: プラットフォーム別Passkey実装
        _logger.LogWarning("Passkey login not yet implemented for native");
        await Task.CompletedTask;
        return false;
    }
}

// ILoggerの模擬実装 (実際はMicrosoft.Extensions.Loggingを使用)
file class ILogger<T>
{
    public void LogInformation(string message, params object[] args) { }
    public void LogWarning(string message, params object[] args) { }
    public void LogError(Exception ex, string message, params object[] args) { }
}
