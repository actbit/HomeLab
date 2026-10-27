using HomeLab.Server.Data;
using HomeLab.Server.Models.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HomeLab.Server.Tests;

public sealed class SqliteMigrationTests
{
    private const string Initial = "20260607112553_InitialCreate";
    private static AppDbContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

    [Fact]
    public async Task EmptyDatabaseRunsMigrationsAndCanRestart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await SqliteMigrationInitializer.InitializeAsync(db);
        Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
        await SqliteMigrationInitializer.InitializeAsync(db);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task EnsureCreatedDatabaseCanBeAdoptedWithoutRecreatingTables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new AppUser { Id = "existing", UserName = "existing" });
        await db.SaveChangesAsync();
        await SqliteMigrationInitializer.InitializeAsync(db);
        Assert.NotNull(await db.Users.FindAsync("existing"));
        Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Fact]
    public async Task DatabaseAlreadyUsingInitialMigrationIsUpgraded()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.GetService<IMigrator>().MigrateAsync(Initial);
        await SqliteMigrationInitializer.InitializeAsync(db);
        Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldEnsureCreatedDatabasePreservesRowsAndExistingTemporaryKeys(bool hasTemporaryKeys)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.GetService<IMigrator>().MigrateAsync(Initial);
        var user = new AppUser { Id = "owner", UserName = "owner", CreatedAt = DateTimeOffset.UtcNow };
        var device = new LockDevice { Id = Guid.NewGuid(), UserId = user.Id, Name = "front door", DeviceIdentifier = "device-1", DevicePublicKey = "public", SesameDeviceUuid = "uuid", Status = "Offline" };
        db.Users.Add(user);
        db.LockDevices.Add(device);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\"");
        var keyId = Guid.NewGuid();
        if (hasTemporaryKeys)
        {
            await TemporaryKeySchema.EnsureAsync(db);
            db.TemporaryKeys.Add(new TemporaryKey { Id = keyId, DeviceId = device.Id, TokenHash = new string('A', 64), Label = "guest", RemainingUses = 3, ExpiresAtUnix = 2000000000 });
            await db.SaveChangesAsync();
        }
        await SqliteMigrationInitializer.InitializeAsync(db);
        Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal("front door", (await db.LockDevices.FindAsync(device.Id))!.Name);
        if (hasTemporaryKeys) Assert.Equal(3, (await db.TemporaryKeys.FindAsync(keyId))!.RemainingUses);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task IncompatibleOldSchemaIsNotMarkedAsMigrated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.GetService<IMigrator>().MigrateAsync(Initial);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\"");
        await db.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_LockDevices_DeviceIdentifier\"");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteMigrationInitializer.InitializeAsync(db));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task UnrelatedDatabaseIsNotBaselined()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE Unrelated (Id INTEGER PRIMARY KEY)");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteMigrationInitializer.InitializeAsync(db));
    }

    [Fact]
    public async Task MissingTableWithAppliedHistoryIsRejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"TemporaryKeys\"");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteMigrationInitializer.InitializeAsync(db));
    }

    [Fact]
    public async Task EfMigrationDoesNotMarkAnIncompleteTemporaryKeyTableAsApplied()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.GetService<IMigrator>().MigrateAsync(Initial);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"TemporaryKeys\" (\"Id\" TEXT PRIMARY KEY, \"DeviceId\" TEXT NOT NULL, \"TokenHash\" TEXT NOT NULL)");
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
        Assert.Equal(new[] { Initial }, await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public void SqliteMigrationsCannotBeAppliedToPostgres()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options;
        using var db = new AppDbContext(options);
        Assert.Throws<NotSupportedException>(() => db.GetService<IMigrator>().GenerateScript());
    }
}
