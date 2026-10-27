using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HomeLab.Server.Data;

/// <summary>Adopts the old EnsureCreated SQLite schema only after checking its structure.</summary>
public static class SqliteMigrationInitializer
{
    private const string InitialMigration = "20260607112553_InitialCreate";
    private const string TemporaryKeysMigration = "20261027163057_AddTemporaryKeys";
    private const string History = "__EFMigrationsHistory";

    public static async Task InitializeAsync(AppDbContext db)
    {
        if (!db.Database.IsSqlite()) throw new InvalidOperationException("SQLite database required.");
        await db.Database.OpenConnectionAsync();
        try
        {
            var connection = db.Database.GetDbConnection();
            var actualTables = await TablesAsync(connection);
            var hasHistory = actualTables.Contains(History);
            var applied = hasHistory ? await QueryAsync(connection, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"", null) : [];
            var hasInitial = applied.Any(row => row[0] == InitialMigration);
            var hasTemporaryMigration = applied.Any(row => row[0] == TemporaryKeysMigration);
            var knownMigrations = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            if (applied.Any(row => !knownMigrations.Contains(row[0])))
                throw new InvalidOperationException("SQLite contains an unrecognized migration history; upgrade the application first.");
            var applicationTables = actualTables.Where(t => t != History && t != "__EFMigrationsLock").ToHashSet();

            if (applicationTables.Count == 0)
            {
                if (applied.Count != 0) throw new InvalidOperationException("Migration history exists but the application tables are missing.");
                await db.Database.MigrateAsync();
                return;
            }

            // Once a database is under EF migration control, later migrations may
            // legitimately change the original schema. Do not re-baseline it.
            var pendingExistingTemporaryTable = hasInitial && !hasTemporaryMigration && actualTables.Contains("TemporaryKeys");
            if (hasInitial && !pendingExistingTemporaryTable)
            {
                var hasLaterMigration = applied.Any(row => string.CompareOrdinal(row[0], TemporaryKeysMigration) > 0);
                if (hasTemporaryMigration && !hasLaterMigration && !actualTables.Contains("TemporaryKeys"))
                    throw new InvalidOperationException("Migration history claims TemporaryKeys exists, but its table is missing.");
                await db.Database.MigrateAsync();
                return;
            }

            // Compare against the actual initial migration in an isolated in-memory database.
            // Missing or altered columns, foreign keys or indexes are not silently baselined.
            await using var scratchConnection = new SqliteConnection("Data Source=:memory:");
            await scratchConnection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(scratchConnection).Options;
            await using var scratch = new AppDbContext(options);
            await scratch.GetService<IMigrator>().MigrateAsync(InitialMigration);
            var expectedTables = await TablesAsync(scratchConnection);
            foreach (var table in expectedTables.Where(t => !hasInitial && t != History && t != "__EFMigrationsLock"))
            {
                if (!actualTables.Contains(table)) throw new InvalidOperationException($"Cannot migrate SQLite: required table '{table}' is missing.");
                await ValidateTableAsync(scratchConnection, connection, table);
            }
            if (actualTables.Contains("TemporaryKeys"))
            {
                await scratch.GetService<IMigrator>().MigrateAsync(TemporaryKeysMigration);
                await ValidateTableAsync(scratchConnection, connection, "TemporaryKeys");
            }
            else if (hasTemporaryMigration)
                throw new InvalidOperationException("Migration history claims TemporaryKeys exists, but its table is missing.");

            if (!hasInitial)
            {
                if (applied.Count != 0) throw new InvalidOperationException("Cannot baseline an unrecognized migration history.");
                // This is additive and idempotent; no existing tables or rows are modified.
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                        "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                        "ProductVersion" TEXT NOT NULL
                    )
                    """);
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260607112553_InitialCreate', '10.0.8')");
            }
            await db.Database.MigrateAsync();
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static async Task<HashSet<string>> TablesAsync(DbConnection db)
        => (await QueryAsync(db, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'", null))
            .Select(row => row[0]).ToHashSet(StringComparer.Ordinal);

    private static async Task ValidateTableAsync(DbConnection expected, DbConnection actual, string table)
    {
        const string columns = "SELECT name, type, \"notnull\", pk FROM pragma_table_info($name)";
        var expectedColumns = await QueryAsync(expected, columns, table);
        var actualColumns = await QueryAsync(actual, columns, table);
        foreach (var column in expectedColumns)
        {
            var found = actualColumns.FirstOrDefault(c => c[0] == column[0]);
            if (found is null || Affinity(found[1]) != Affinity(column[1]) || found[2] != column[2] || found[3] != column[3])
                throw new InvalidOperationException($"Cannot migrate SQLite: column '{table}.{column[0]}' is missing or incompatible.");
        }
        const string foreignKeys = "SELECT \"table\", \"from\", \"to\", on_delete FROM pragma_foreign_key_list($name)";
        var expectedFks = await QueryAsync(expected, foreignKeys, table);
        var actualFks = await QueryAsync(actual, foreignKeys, table);
        if (expectedFks.Any(fk => !actualFks.Any(actualFk => actualFk.SequenceEqual(fk))))
            throw new InvalidOperationException($"Cannot migrate SQLite: foreign keys of '{table}' are incompatible.");

        var expectedIndexes = await IndexesAsync(expected, table);
        var actualIndexes = await IndexesAsync(actual, table);
        if (!expectedIndexes.IsSubsetOf(actualIndexes))
            throw new InvalidOperationException($"Cannot migrate SQLite: indexes of '{table}' are missing or incompatible.");
    }

    private static async Task<HashSet<string>> IndexesAsync(DbConnection db, string table)
    {
        var indexes = await QueryAsync(db, "SELECT name, \"unique\", partial FROM pragma_index_list($name) WHERE origin != 'pk'", table);
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in indexes)
        {
            var columns = await QueryAsync(db, "SELECT name FROM pragma_index_info($name) ORDER BY seqno", index[0]);
            signatures.Add($"{index[1]}:{index[2]}:{string.Join(',', columns.Select(c => c[0]))}");
        }
        return signatures;
    }

    private static string Affinity(string type)
    {
        type = type.ToUpperInvariant();
        if (type.Contains("INT")) return "INTEGER";
        if (type.Contains("CHAR") || type.Contains("CLOB") || type.Contains("TEXT")) return "TEXT";
        if (type.Contains("BLOB") || type.Length == 0) return "BLOB";
        if (type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB")) return "REAL";
        return "NUMERIC";
    }

    private static async Task<List<string[]>> QueryAsync(DbConnection db, string sql, string? name)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        if (name is not null)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = name;
            command.Parameters.Add(parameter);
        }
        var rows = new List<string[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
            rows.Add(row);
        }
        return rows;
    }
}
