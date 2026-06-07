using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using HomeLab.Server.Data;

namespace HomeLab.Server.Authentication;

/// <summary>
/// ESP32デバイス署名認証ハンドラー
/// Authorization: Device {deviceId}:{base64(signature)}
/// signature = ECDSA_P256(payload, devicePrivateKey)
/// </summary>
public class EspDeviceAuthHandler : AuthenticationHandler<EspDeviceAuthOptions>
{
    private readonly AppDbContext _dbContext;

    public EspDeviceAuthHandler(
        IOptionsMonitor<EspDeviceAuthOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        AppDbContext dbContext)
        : base(options, logger, encoder)
    {
        _dbContext = dbContext;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Device ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var parts = authHeader["Device ".Length..].Split(':');
        if (parts.Length != 2)
        {
            return AuthenticateResult.Fail("Invalid device authentication format");
        }

        var deviceId = parts[0];
        var signatureBase64 = parts[1];

        // デバイスを検索
        var device = await _dbContext.LockDevices
            .FirstOrDefaultAsync(d => d.DeviceIdentifier == deviceId);

        if (device is null)
        {
            return AuthenticateResult.Fail("Device not found");
        }

        // ペイロードを再構築 (リクエストメソッド + パス + ボディハッシュ)
        var method = Request.Method;
        var path = Request.Path;
        var timestamp = Request.Headers["X-Timestamp"].ToString();

        // ボディを読み取り (必要に応じて)
        Request.EnableBuffering();
        Request.Body.Position = 0;
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        Request.Body.Position = 0;

        var payload = $"{method}\n{path}\n{timestamp}\n{body}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var signatureBytes = Convert.FromBase64String(signatureBase64);

        // ECDSA P-256 公開鍵で署名検証
        try
        {
            using var ecdsa = ECDsa.Create();
            var publicKeyBytes = Convert.FromBase64String(device.DevicePublicKey);
            ecdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);

            if (!ecdsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256))
            {
                return AuthenticateResult.Fail("Invalid device signature");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Device signature verification failed for device {DeviceId}", deviceId);
            return AuthenticateResult.Fail("Signature verification error");
        }

        // 認証成功 - Claims生成
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, device.UserId),
            new("device_id", device.Id.ToString()),
            new("device_identifier", deviceId),
            new("tenant_id", device.UserId),
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        // LastSeenAt を更新
        device.LastSeenAt = DateTimeOffset.UtcNow;
        device.Status = "Online";
        await _dbContext.SaveChangesAsync();

        return AuthenticateResult.Success(ticket);
    }
}

/// <summary>
/// ESP32デバイス認証オプション
/// </summary>
public class EspDeviceAuthOptions : AuthenticationSchemeOptions
{
}
