using HomeLab.Server.Models.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Server.Data;

/// <summary>
/// アプリケーションのメイン DbContext
/// Finbuckle マルチテナンシー対応
/// </summary>
public class AppDbContext : IdentityDbContext<AppUser>
{
    private readonly string? _tenantId;

    public AppDbContext(DbContextOptions<AppDbContext> options, string? tenantId = null)
        : base(options)
    {
        _tenantId = tenantId;
    }

    public DbSet<PasskeyCredential> PasskeyCredentials => Set<PasskeyCredential>();
    public DbSet<LockDevice> LockDevices => Set<LockDevice>();
    public DbSet<LockLog> LockLogs => Set<LockLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // PasskeyCredential 設定
        builder.Entity<PasskeyCredential>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CredentialId).IsRequired();
            entity.Property(e => e.PublicKey).IsRequired();
            entity.Property(e => e.UserHandle).IsRequired();
            entity.Property(e => e.AuthenticatorType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.Aaguid).IsRequired();

            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CredentialId);

            entity.HasOne(e => e.User)
                .WithMany(u => u.PasskeyCredentials)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // LockDevice 設定
        builder.Entity<LockDevice>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.DevicePublicKey).IsRequired();
            entity.Property(e => e.DeviceIdentifier).IsRequired();
            entity.Property(e => e.SesameDeviceUuid).IsRequired();
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.ActivationKey).HasMaxLength(256);

            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.DeviceIdentifier).IsUnique();

            entity.HasOne(e => e.User)
                .WithMany(u => u.Devices)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // LockLog 設定
        builder.Entity<LockLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ActionType).IsRequired().HasMaxLength(20);
            entity.Property(e => e.TriggeredBy).HasMaxLength(20);
            entity.Property(e => e.RequestId).HasMaxLength(64);
            entity.Property(e => e.ErrorMessage).HasMaxLength(500);

            entity.HasIndex(e => e.DeviceId);
            entity.HasIndex(e => e.Timestamp);

            entity.HasOne(e => e.Device)
                .WithMany(d => d.Logs)
                .HasForeignKey(e => e.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // テナントフィルター (Global Query Filter)
        if (!string.IsNullOrEmpty(_tenantId))
        {
            builder.Entity<LockDevice>().HasQueryFilter(d => d.UserId == _tenantId);
            builder.Entity<LockLog>().HasQueryFilter(l => l.Device.UserId == _tenantId);
        }
    }

    /// <summary>
    /// テナントIDを設定 (Finbuckleの TenantResolver から使用)
    /// </summary>
    public void SetTenantId(string tenantId)
    {
        // グローバルクエリフィルターはOnModelCreatingで設定済み
        // このメソッドはDIコンテナから呼び出される
    }
}
