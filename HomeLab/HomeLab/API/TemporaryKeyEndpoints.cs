using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Fido2NetLib;
using Fido2NetLib.Objects;
using HomeLab.Server.Authentication.Passkey;
using HomeLab.Server.Data;
using HomeLab.Server.Models.Entities;
using HomeLab.Server.Services;
using HomeLab.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Server.API;

public static class TemporaryKeyEndpoints
{
    public sealed record CreateKey(string Label, int Uses, int ValidHours);
    public sealed record KeyToken(string Token);
    public sealed record Registration(string Token, string ChallengeId, AuthenticatorAttestationRawResponse Credential);
    public sealed record Unlock(string Token, string ChallengeId, AuthenticatorAssertionRawResponse Credential);

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static bool ValidToken(string? token) => token is { Length: 64 } && token.All(Uri.IsHexDigit);
    private static string ChallengeOwner(Guid id, string action) => $"temporary-key:{id:N}:{action}";
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static WebApplication MapTemporaryKeyEndpoints(this WebApplication app)
    {
        var owner = app.MapGroup("/api/devices/{deviceId:guid}/temporary-keys").RequireAuthorization();
        owner.MapGet("/", async (Guid deviceId, AppDbContext db, HttpContext ctx) =>
        {
            var user = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!await db.LockDevices.AnyAsync(d => d.Id == deviceId && d.UserId == user)) return Results.NotFound();
            var keys = await db.TemporaryKeys.Where(k => k.DeviceId == deviceId).ToListAsync();
            return Results.Ok(keys.Select(k => new { k.Id, k.Label, k.RemainingUses, k.ExpiresAtUnix, k.Revoked, Registered = k.CredentialId != null }));
        });
        owner.MapPost("/", async (Guid deviceId, CreateKey request, AppDbContext db, HttpContext ctx) =>
        {
            var user = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!await db.LockDevices.AnyAsync(d => d.Id == deviceId && d.UserId == user)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Label) || request.Label.Trim().Length > 100 || request.Uses is < 1 or > 1000 || request.ValidHours is < 1 or > 720)
                return Results.BadRequest(new { error = "名前、回数（1〜1000）、有効時間（1〜720時間）を確認してください。" });
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var key = new TemporaryKey { Id = Guid.NewGuid(), DeviceId = deviceId, TokenHash = Hash(token), Label = request.Label.Trim(), RemainingUses = request.Uses, ExpiresAtUnix = Now + request.ValidHours * 3600L };
            db.TemporaryKeys.Add(key);
            await db.SaveChangesAsync();
            return Results.Ok(new { key.Id, Token = token });
        });
        owner.MapDelete("/{id:guid}", async (Guid deviceId, Guid id, AppDbContext db, HttpContext ctx, IHubContext<TemporaryKeyHub> hub) =>
        {
            var user = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var key = await db.TemporaryKeys.FirstOrDefaultAsync(k => k.Id == id && k.DeviceId == deviceId && k.Device.UserId == user);
            if (key is null) return Results.NotFound();
            key.Revoked = true;
            await db.SaveChangesAsync();
            await hub.Clients.Group($"temporary:{key.Id:N}").SendAsync("Updated");
            return Results.NoContent();
        });

        var guest = app.MapGroup("/api/temporary-keys").RequireRateLimiting("authentication");
        guest.MapPost("/status", async (KeyToken request, AppDbContext db, DeviceConnectionService connections) =>
        {
            var key = await FindAsync(db, request.Token);
            if (key is null) return Results.NotFound(new { error = "一時キーが無効か期限切れです。" });
            var log = key.LastRequestId is null ? null : await db.LockLogs.AsNoTracking().FirstOrDefaultAsync(l => l.DeviceId == key.DeviceId && l.RequestId == key.LastRequestId);
            return Results.Ok(new { key.Label, DeviceName = key.Device.Name, key.RemainingUses, key.ExpiresAtUnix, Registered = key.CredentialId != null,
                Online = connections.IsDeviceOnline(key.DeviceId.ToString()), IsLocked = connections.GetKnownLockState(key.DeviceId),
                LastResult = log is null ? null : log.ErrorMessage == DeviceConnectionService.PendingCommand ? "pending" : log.Success ? "success" : "failed" });
        });
        guest.MapPost("/register-options", async (KeyToken request, AppDbContext db, IFido2 fido, PasskeyChallengeStore challenges) =>
        {
            var key = await FindAsync(db, request.Token);
            if (key is null || key.CredentialId != null || key.RemainingUses <= 0) return Results.NotFound();
            var options = fido.RequestNewCredential(new RequestNewCredentialParams {
                User = new Fido2User { Id = key.Id.ToByteArray(), Name = $"temporary-{key.Id:N}", DisplayName = key.Label },
                AuthenticatorSelection = new AuthenticatorSelection { ResidentKey = ResidentKeyRequirement.Required, UserVerification = UserVerificationRequirement.Required },
                AttestationPreference = AttestationConveyancePreference.None
            });
            return Results.Ok(new { ChallengeId = challenges.Create(options, ChallengeOwner(key.Id, "register")), Options = options });
        });
        guest.MapPost("/register", async (Registration request, AppDbContext db, IFido2 fido, PasskeyChallengeStore challenges) =>
        {
            var key = await FindAsync(db, request.Token);
            if (key is null || key.CredentialId != null || key.RemainingUses <= 0) return Results.NotFound();
            var options = challenges.Consume<CredentialCreateOptions>(request.ChallengeId, ChallengeOwner(key.Id, "register"));
            if (options is null || request.Credential is null) return Results.BadRequest();
            try
            {
                var result = await fido.MakeNewCredentialAsync(new MakeNewCredentialParams {
                    AttestationResponse = request.Credential, OriginalOptions = options,
                    IsCredentialIdUniqueToUserCallback = async (args, ct) => {
                        var id = Convert.ToBase64String(args.CredentialId);
                        return !await db.TemporaryKeys.AnyAsync(k => k.CredentialId == id, ct);
                    }
                });
                var credential = Convert.ToBase64String(result.Id);
                var publicKey = Convert.ToBase64String(result.PublicKey);
                var now = Now;
                var updated = await db.TemporaryKeys.Where(k => k.Id == key.Id && !k.Revoked && k.ExpiresAtUnix > now && k.RemainingUses > 0 && k.CredentialId == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(k => k.CredentialId, credential).SetProperty(k => k.PublicKey, publicKey).SetProperty(k => k.SignatureCounter, (long)result.SignCount));
                return updated == 1 ? Results.Ok() : Results.Conflict();
            }
            catch (Exception) { return Results.BadRequest(new { error = "パスキーを登録できませんでした。" }); }
        });
        guest.MapPost("/unlock-options", async (KeyToken request, AppDbContext db, IFido2 fido, PasskeyChallengeStore challenges) =>
        {
            var key = await FindAsync(db, request.Token);
            if (key is null || key.CredentialId is null || key.RemainingUses <= 0) return Results.NotFound();
            var options = fido.GetAssertionOptions(new GetAssertionOptionsParams {
                AllowedCredentials = [new PublicKeyCredentialDescriptor(Convert.FromBase64String(key.CredentialId))],
                UserVerification = UserVerificationRequirement.Required
            });
            return Results.Ok(new { ChallengeId = challenges.Create(options, ChallengeOwner(key.Id, "unlock")), Options = options });
        });
        guest.MapPost("/unlock", async (Unlock request, AppDbContext db, IFido2 fido, PasskeyChallengeStore challenges, DeviceConnectionService connections) =>
        {
            var key = await FindAsync(db, request.Token);
            if (key is null || key.CredentialId is null || key.PublicKey is null || key.RemainingUses <= 0) return Results.NotFound();
            var options = challenges.Consume<AssertionOptions>(request.ChallengeId, ChallengeOwner(key.Id, "unlock"));
            if (options is null || request.Credential is null) return Results.BadRequest();
            if (!connections.IsDeviceOnline(key.DeviceId.ToString())) return Results.BadRequest(new { error = "デバイスがオフラインです。回数は消費されません。" });
            try
            {
                var credentialId = Convert.FromBase64String(key.CredentialId);
                if (!credentialId.SequenceEqual(request.Credential.RawId)) return Results.BadRequest();
                var result = await fido.MakeAssertionAsync(new MakeAssertionParams {
                    AssertionResponse = request.Credential, OriginalOptions = options,
                    StoredPublicKey = Convert.FromBase64String(key.PublicKey), StoredSignatureCounter = checked((uint)key.SignatureCounter),
                    IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(args.UserHandle.SequenceEqual(key.Id.ToByteArray()) && args.CredentialId.SequenceEqual(credentialId))
                });
                var requestId = Guid.NewGuid().ToString("N")[..16];
                var now = Now;
                await using var transaction = await db.Database.BeginTransactionAsync();
                var updated = await db.TemporaryKeys.Where(k => k.Id == key.Id && !k.Revoked && k.ExpiresAtUnix > now && k.RemainingUses > 0 && k.CredentialId == key.CredentialId && k.SignatureCounter == key.SignatureCounter)
                    .ExecuteUpdateAsync(s => s.SetProperty(k => k.RemainingUses, k => k.RemainingUses - 1).SetProperty(k => k.SignatureCounter, (long)result.SignCount).SetProperty(k => k.LastRequestId, requestId));
                if (updated != 1) return Results.Conflict(new { error = "回数を使い切ったか、キーが無効になりました。" });
                db.LockLogs.Add(new LockLog { Id = Guid.NewGuid(), DeviceId = key.DeviceId, ActionType = "Unlock", TriggeredBy = $"Key:{key.Id:N}"[..20], RequestId = requestId, ErrorMessage = DeviceConnectionService.PendingCommand, Timestamp = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                var sent = await connections.SendCommandAsync(key.DeviceId, "unlock", requestId);
                if (!sent)
                {
                    var log = await db.LockLogs.FirstAsync(l => l.DeviceId == key.DeviceId && l.RequestId == requestId);
                    log.ErrorMessage = "Device not connected";
                    await db.SaveChangesAsync();
                    return Results.BadRequest(new { error = "送信できませんでした。安全のため回数は消費されました。" });
                }
                return Results.Ok(new { requestId });
            }
            catch (Fido2VerificationException) { return Results.BadRequest(new { error = "パスキーを検証できませんでした。" }); }
        });
        return app;
    }

    private static Task<TemporaryKey?> FindAsync(AppDbContext db, string? token)
    {
        if (!ValidToken(token)) return Task.FromResult<TemporaryKey?>(null);
        var hash = Hash(token!);
        var now = Now;
        return db.TemporaryKeys.AsNoTracking().Include(k => k.Device).FirstOrDefaultAsync(k => k.TokenHash == hash && !k.Revoked && k.ExpiresAtUnix > now);
    }
}
