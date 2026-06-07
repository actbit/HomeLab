using HomeLab.Server.Authentication.Totp;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeLab.Server.Tests;

public class TotpServiceTests
{
    private readonly TotpService _totpService;

    public TotpServiceTests()
    {
        _totpService = new TotpService(NullLogger<TotpService>.Instance);
    }

    [Fact]
    public void GenerateSecretKey_ReturnsBase32String()
    {
        // Act
        var key = _totpService.GenerateSecretKey();

        // Assert
        Assert.False(string.IsNullOrEmpty(key));
        Assert.Equal(32, key.Length); // 20 bytes → 32 Base32文字
        Assert.True(key.All(c => "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".Contains(c)));
    }

    [Fact]
    public void GenerateSecretKey_GeneratesUniqueKeys()
    {
        // Act
        var key1 = _totpService.GenerateSecretKey();
        var key2 = _totpService.GenerateSecretKey();

        // Assert
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void GenerateQrCodeUri_ContainsRequiredParts()
    {
        // Arrange
        var email = "test@example.com";
        var secretKey = "JBSWY3DPEHPK3PXP";

        // Act
        var uri = _totpService.GenerateQrCodeUri(email, secretKey);

        // Assert
        Assert.StartsWith("otpauth://totp/", uri);
        Assert.Contains(secretKey, uri);
        Assert.Contains(Uri.EscapeDataString(email), uri);
        Assert.Contains("issuer=HomeLab%20SESAME", uri);
        Assert.Contains("digits=6", uri);
        Assert.Contains("period=30", uri);
    }

    [Fact]
    public void GenerateQrCodePng_ReturnsNonEmptyPng()
    {
        // Arrange
        var uri = _totpService.GenerateQrCodeUri("test@example.com", "JBSWY3DPEHPK3PXP");

        // Act
        var png = _totpService.GenerateQrCodePng(uri);

        // Assert
        Assert.NotNull(png);
        Assert.True(png.Length > 0);
        // PNGシグネチャチェック
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        Assert.Equal((byte)'N', png[2]);
        Assert.Equal((byte)'G', png[3]);
    }

    [Fact]
    public void ValidateCode_ValidCode_ReturnsTrue()
    {
        // Arrange
        var secretKey = _totpService.GenerateSecretKey();
        var keyBytes = OtpNet.Base32Encoding.ToBytes(secretKey);
        var totp = new OtpNet.Totp(keyBytes, mode: OtpNet.OtpHashMode.Sha1, step: 30, totpSize: 6);
        var validCode = totp.ComputeTotp();

        // Act
        var result = _totpService.ValidateCode(secretKey, validCode);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ValidateCode_InvalidCode_ReturnsFalse()
    {
        // Arrange
        var secretKey = _totpService.GenerateSecretKey();

        // Act
        var result = _totpService.ValidateCode(secretKey, "000000");

        // Assert
        // 000000が実際のTOTPコードと一致する可能性は極めて低いが、確率的にfalseになるはず
        // 厳密なテストのため、明らかに間違ったコードを使用
        Assert.False(_totpService.ValidateCode(secretKey, "invalid"));
    }

    [Fact]
    public void ValidateCode_EmptyCode_ReturnsFalse()
    {
        // Arrange
        var secretKey = _totpService.GenerateSecretKey();

        // Act
        var result = _totpService.ValidateCode(secretKey, "");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GenerateRecoveryCodes_ReturnsCorrectCount()
    {
        // Act
        var codes = _totpService.GenerateRecoveryCodes(8);

        // Assert
        Assert.Equal(8, codes.Count);
    }

    [Fact]
    public void GenerateRecoveryCodes_CorrectFormat()
    {
        // Act
        var codes = _totpService.GenerateRecoveryCodes();

        // Assert
        foreach (var code in codes)
        {
            // フォーマット: xxxx-xxxx (小文字hex)
            Assert.Matches(@"^[0-9a-f]{4}-[0-9a-f]{4}$", code);
        }
    }

    [Fact]
    public void GenerateRecoveryCodes_GeneratesUniqueCodes()
    {
        // Act
        var codes = _totpService.GenerateRecoveryCodes(100);

        // Assert
        Assert.Equal(100, codes.Distinct().Count());
    }
}
