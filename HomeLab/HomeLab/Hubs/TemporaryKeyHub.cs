using System.Security.Cryptography;
using System.Text;
using HomeLab.Server.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Server.Hubs;

public sealed class TemporaryKeyHub(AppDbContext db) : Hub
{
    public async Task Join(string token)
    {
        if (token is not { Length: 64 } || !token.All(Uri.IsHexDigit)) throw new HubException("Invalid key");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var key = await db.TemporaryKeys.AsNoTracking().FirstOrDefaultAsync(k => k.TokenHash == hash && !k.Revoked && k.ExpiresAtUnix > now);
        if (key is null) throw new HubException("Invalid key");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"temporary:{key.Id:N}");
    }
}
