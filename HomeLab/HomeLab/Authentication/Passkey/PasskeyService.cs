using Fido2NetLib;
using Fido2NetLib.Objects;
using HomeLab.Server.Data;
using HomeLab.Server.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Server.Authentication.Passkey;

/// <summary>
/// FIDO2/WebAuthn Passkey認証サービス (Fido2 v4)
/// </summary>
public class PasskeyService
{
    private readonly IFido2 _fido2;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<PasskeyService> _logger;

    public PasskeyService(IFido2 fido2, AppDbContext dbContext, ILogger<PasskeyService> logger)
    {
        _fido2 = fido2;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<CredentialCreateOptions> CreateRegistrationOptionsAsync(AppUser user, string? deviceName = null)
    {
        var existingCredentials = await _dbContext.PasskeyCredentials
            .Where(c => c.UserId == user.Id)
            .Select(c => c.CredentialId)
            .ToListAsync();

        var fidoUser = new Fido2User
        {
            Id = System.Text.Encoding.UTF8.GetBytes(user.Id),
            Name = user.Email ?? user.UserName ?? string.Empty,
            DisplayName = user.DisplayName ?? user.Email ?? user.UserName ?? string.Empty,
        };

        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = fidoUser,
            ExcludeCredentials = existingCredentials
                .Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        return options;
    }

    public async Task<PasskeyCredential> CompleteRegistrationAsync(
        AppUser user, AuthenticatorAttestationRawResponse attestationResponse,
        CredentialCreateOptions options, string? deviceName = null)
    {
        var result = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
        {
            AttestationResponse = attestationResponse,
            OriginalOptions = options,
            IsCredentialIdUniqueToUserCallback = async (args, ct) =>
            {
                var ids = await _dbContext.PasskeyCredentials.Select(c => c.CredentialId).ToListAsync(ct);
                return !ids.Any(id => id.SequenceEqual(args.CredentialId));
            }
        });

        var credential = new PasskeyCredential
        {
            UserId = user.Id,
            CredentialId = result.Id,
            PublicKey = result.PublicKey,
            UserHandle = result.User.Id,
            SignatureCounter = result.SignCount,
            AuthenticatorType = result.Type.ToString(),
            Name = deviceName,
            Aaguid = result.AaGuid.ToByteArray(),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.PasskeyCredentials.Add(credential);
        await _dbContext.SaveChangesAsync();
        return credential;
    }

    public async Task<AssertionOptions> CreateLoginOptionsAsync(string? userId = null)
    {
        var existingCredentials = new List<PublicKeyCredentialDescriptor>();

        if (!string.IsNullOrEmpty(userId))
        {
            existingCredentials = await _dbContext.PasskeyCredentials
                .Where(c => c.UserId == userId)
                .Select(c => c.CredentialId)
                .Select(id => new PublicKeyCredentialDescriptor(id))
                .ToListAsync();
        }

        return _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = existingCredentials,
            UserVerification = UserVerificationRequirement.Required,
        });
    }

    public async Task<AppUser?> CompleteLoginAsync(AuthenticatorAssertionRawResponse assertionResponse, AssertionOptions options)
    {
        var credentials = await _dbContext.PasskeyCredentials.Include(c => c.User).ToListAsync();
        var credential = credentials.FirstOrDefault(c => c.CredentialId.SequenceEqual(assertionResponse.RawId));

        if (credential is null) return null;

        var result = await _fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = assertionResponse,
            OriginalOptions = options,
            StoredPublicKey = credential.PublicKey,
            StoredSignatureCounter = credential.SignatureCounter,
            IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(
                credential.UserHandle.SequenceEqual(args.UserHandle) && credential.CredentialId.SequenceEqual(args.CredentialId))
        });

        credential.SignatureCounter = result.SignCount;
        credential.LastUsedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync();
        return credential.User;
    }

    public async Task<List<PasskeyCredential>> GetUserPasskeysAsync(string userId)
    {
        var credentials = await _dbContext.PasskeyCredentials
            .Where(c => c.UserId == userId)
            .ToListAsync();
        return credentials.OrderByDescending(c => c.LastUsedAt ?? c.CreatedAt).ToList();
    }

    public async Task DeletePasskeyAsync(Guid credentialId, string userId)
    {
        var credential = await _dbContext.PasskeyCredentials
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.UserId == userId);

        if (credential is not null)
        {
            _dbContext.PasskeyCredentials.Remove(credential);
            await _dbContext.SaveChangesAsync();
        }
    }
}
