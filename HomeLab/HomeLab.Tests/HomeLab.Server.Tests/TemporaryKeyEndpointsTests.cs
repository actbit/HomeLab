using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomeLab.Server.Data;
using Microsoft.Extensions.DependencyInjection;

namespace HomeLab.Server.Tests;

public sealed class TemporaryKeyEndpointsTests
{
    [Fact]
    public async Task OwnerCanCreateAndRevokeKeyWithoutStoringItsSecret()
    {
        await using var factory = new CustomWebApplicationFactory();
        using var owner = factory.CreateClient();
        using var other = factory.CreateClient();
        using var guest = factory.CreateClient();
        var (ownerId, ownerToken) = await factory.CreateTestUserAsync("temporary-owner@test.com");
        var (_, otherToken) = await factory.CreateTestUserAsync("temporary-other@test.com");
        var device = await factory.CreateTestDeviceAsync(ownerId, status: "Offline");
        owner.DefaultRequestHeaders.Authorization = new("Bearer", ownerToken);
        other.DefaultRequestHeaders.Authorization = new("Bearer", otherToken);
        var url = $"/api/devices/{device.Id}/temporary-keys";
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(url, new { label = "guest", uses = 1, validHours = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(url, new { label = "guest", uses = 0, validHours = 1 })).StatusCode);
        var created = await owner.PostAsJsonAsync(url, new { label = "guest", uses = 2, validHours = 1 });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var token = body.RootElement.GetProperty("token").GetString()!;
        var id = body.RootElement.GetProperty("id").GetGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var key = await db.TemporaryKeys.FindAsync(id);
            Assert.NotNull(key);
            Assert.NotEqual(token, key.TokenHash);
            Assert.Equal(2, key.RemainingUses);
        }
        Assert.Equal(HttpStatusCode.OK, (await guest.PostAsJsonAsync("/api/temporary-keys/status", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.PostAsJsonAsync("/api/temporary-keys/register-options", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"{url}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{url}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsJsonAsync("/api/temporary-keys/status", new { token })).StatusCode);
    }

    [Fact]
    public async Task ExpiredKeyCannotRegisterOrShowStatus()
    {
        await using var factory = new CustomWebApplicationFactory();
        using var owner = factory.CreateClient();
        var (userId, jwt) = await factory.CreateTestUserAsync("temporary-expired@test.com");
        var device = await factory.CreateTestDeviceAsync(userId, status: "Offline");
        owner.DefaultRequestHeaders.Authorization = new("Bearer", jwt);
        var response = await owner.PostAsJsonAsync($"/api/devices/{device.Id}/temporary-keys", new { label = "expired", uses = 1, validHours = 1 });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = body.RootElement.GetProperty("token").GetString()!;
        var id = body.RootElement.GetProperty("id").GetGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.TemporaryKeys.FindAsync(id))!.ExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
            await db.SaveChangesAsync();
        }
        using var guest = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsJsonAsync("/api/temporary-keys/status", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsJsonAsync("/api/temporary-keys/register-options", new { token })).StatusCode);
    }
}
