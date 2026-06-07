using OtpNet;
using QRCoder;
using System.Text;

namespace HomeLab.Server.Authentication.Totp;

/// <summary>
/// TOTP (Time-based One-Time Password) 認証サービス
/// Google Authenticator, Authy 等と互換
/// </summary>
public class TotpService
{
    private readonly ILogger<TotpService> _logger;

    public TotpService(ILogger<TotpService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 新しいTOTP秘密鍵を生成
    /// </summary>
    public string GenerateSecretKey()
    {
        var key = KeyGeneration.GenerateRandomKey(20); // 160 bits
        return Base32Encoding.ToString(key);
    }

    /// <summary>
    /// TOTP QRコード用のURIを生成
    /// </summary>
    public string GenerateQrCodeUri(string email, string secretKey, string issuer = "HomeLab SESAME")
    {
        return $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(email)}" +
               $"?secret={secretKey}" +
               $"&issuer={Uri.EscapeDataString(issuer)}" +
               $"&algorithm=SHA1" +
               $"&digits=6" +
               $"&period=30";
    }

    /// <summary>
    /// QRコード画像をPNGバイト配列として生成
    /// </summary>
    public byte[] GenerateQrCodePng(string qrCodeUri, int pixelsPerModule = 10)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(qrCodeUri, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(qrCodeData);
        return qrCode.GetGraphic(pixelsPerModule);
    }

    /// <summary>
    /// TOTPコードを検証
    /// </summary>
    public bool ValidateCode(string secretKey, string code)
    {
        try
        {
            var key = Base32Encoding.ToBytes(secretKey);
            var totp = new OtpNet.Totp(key, mode: OtpHashMode.Sha1, step: 30, totpSize: 6);

            // 時間ずれを考慮して前後のコードも検証 (±1 window)
            return totp.VerifyTotp(code, out _, new VerificationWindow(1, 1));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TOTP code validation failed");
            return false;
        }
    }

    /// <summary>
    /// リカバリーコードを生成 (TOTPデバイス紛失時用)
    /// </summary>
    public List<string> GenerateRecoveryCodes(int count = 8)
    {
        var codes = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var bytes = new byte[4];
            Random.Shared.NextBytes(bytes);
            var code = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            codes.Add($"{code[..4]}-{code[4..]}");
        }
        return codes;
    }
}
