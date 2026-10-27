using Microsoft.EntityFrameworkCore;

namespace HomeLab.Server.Data;

// EnsureCreated does not add tables to an existing database. This additive upgrade
// supports installations created before temporary keys existed.
public static class TemporaryKeySchema
{
    public static async Task EnsureAsync(AppDbContext db)
    {
        if (!db.Database.IsRelational()) return;
        var postgres = db.Database.IsNpgsql();
        var idType = postgres ? "uuid" : "TEXT";
        var boolType = postgres ? "boolean" : "INTEGER";
        // Both interpolated type names are fixed constants selected above.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"""
            CREATE TABLE IF NOT EXISTS "TemporaryKeys" (
                "Id" {idType} NOT NULL PRIMARY KEY,
                "DeviceId" {idType} NOT NULL,
                "TokenHash" varchar(64) NOT NULL,
                "Label" varchar(100) NOT NULL,
                "ExpiresAtUnix" bigint NOT NULL,
                "RemainingUses" integer NOT NULL,
                "Revoked" {boolType} NOT NULL,
                "CredentialId" TEXT NULL,
                "PublicKey" TEXT NULL,
                "SignatureCounter" bigint NOT NULL,
                "LastRequestId" TEXT NULL,
                FOREIGN KEY ("DeviceId") REFERENCES "LockDevices" ("Id") ON DELETE CASCADE
            )
            """);
#pragma warning restore EF1002
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TemporaryKeys_TokenHash\" ON \"TemporaryKeys\" (\"TokenHash\")");
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_TemporaryKeys_DeviceId\" ON \"TemporaryKeys\" (\"DeviceId\")");
    }
}
