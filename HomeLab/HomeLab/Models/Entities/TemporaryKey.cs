namespace HomeLab.Server.Models.Entities;

public sealed class TemporaryKey
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public LockDevice Device { get; set; } = null!;
    public string TokenHash { get; set; } = "";
    public string Label { get; set; } = "";
    public long ExpiresAtUnix { get; set; }
    public int RemainingUses { get; set; }
    public bool Revoked { get; set; }
    public string? CredentialId { get; set; }
    public string? PublicKey { get; set; }
    public long SignatureCounter { get; set; }
    public string? LastRequestId { get; set; }
}
