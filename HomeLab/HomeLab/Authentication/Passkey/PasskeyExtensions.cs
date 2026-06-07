using Fido2NetLib;
using System.Text;

namespace HomeLab.Server.Authentication.Passkey;

/// <summary>
/// FIDO2 サービス登録拡張メソッド
/// </summary>
public static class PasskeyExtensions
{
    public static IServiceCollection AddPasskeyAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var origin = configuration["Passkey:Origin"] ?? "https://localhost:5001";
        var rpName = configuration["Passkey:RelyingPartyName"] ?? "HomeLab SESAME Control";
        var rpId = configuration["Passkey:RelyingPartyId"] ?? "localhost";

        services.AddSingleton<IFido2>(sp =>
        {
            var options = new Fido2Configuration
            {
                ServerDomain = rpId,
                ServerName = rpName,
                Origins = new HashSet<string>([origin]),
                TimestampDriftTolerance = 300000,
            };

            return new Fido2NetLib.Fido2(options);
        });

        services.AddScoped<PasskeyService>();

        return services;
    }
}
