using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace HomeLab.Server.Authentication.Passkey;

// Each opaque ceremony ID is short-lived and consumed atomically, even on failed verification.
public sealed class PasskeyChallengeStore
{
    private sealed record Challenge(object Options, string? UserId, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<string, Challenge> _challenges = new();

    public string Create(object options, string? userId)
    {
        foreach (var item in _challenges)
            if (item.Value.ExpiresAt <= DateTimeOffset.UtcNow) _challenges.TryRemove(item.Key, out _);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _challenges[id] = new Challenge(options, userId, DateTimeOffset.UtcNow.AddMinutes(3));
        return id;
    }

    public T? Consume<T>(string id, string? userId) where T : class
    {
        if (string.IsNullOrWhiteSpace(id) || !_challenges.TryRemove(id, out var entry) ||
            entry.ExpiresAt <= DateTimeOffset.UtcNow || entry.UserId != userId)
            return null;
        return entry.Options as T;
    }
}
