using System.Security.Claims;
using HomeLab.Server.Hubs;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeLab.Server.Tests;

public sealed class LockHubTests
{
    [Fact]
    public async Task ClientCanOnlyJoinItsOwnTenant()
    {
        var groups = new FakeGroups();
        var hub = new LockHub(NullLogger<LockHub>.Instance)
        {
            Context = new FakeContext("user-a"),
            Groups = groups
        };
        await hub.JoinTenant("user-a");
        Assert.Equal("tenant:user-a", groups.Added);
        await Assert.ThrowsAsync<HubException>(() => hub.JoinTenant("user-b"));
        Assert.Equal("tenant:user-a", groups.Added);
    }

    private sealed class FakeGroups : IGroupManager
    {
        public string? Added { get; private set; }
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        { Added = groupName; return Task.CompletedTask; }
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeContext(string userId) : HubCallerContext
    {
        public override string ConnectionId => "test";
        public override string? UserIdentifier => userId;
        public override ClaimsPrincipal? User => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
