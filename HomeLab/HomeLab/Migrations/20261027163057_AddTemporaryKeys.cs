using System;
using Microsoft.EntityFrameworkCore.Migrations;
using HomeLab.Server.Data;

#nullable disable

namespace HomeLab.Migrations
{
    /// <inheritdoc />
    public partial class AddTemporaryKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!ActiveProvider.Contains("Sqlite", StringComparison.Ordinal))
                throw new NotSupportedException("The existing migration history targets SQLite only.");
            // Previous releases may already have created this table outside EF.
            migrationBuilder.Sql(TemporaryKeySchema.SqliteTable);
            migrationBuilder.Sql("""
                SELECT t."Id", t."DeviceId", t."TokenHash", t."Label", t."ExpiresAtUnix",
                       t."RemainingUses", t."Revoked", t."CredentialId", t."PublicKey",
                       t."SignatureCounter", t."LastRequestId"
                FROM "TemporaryKeys" AS t LIMIT 0
                """);
            migrationBuilder.Sql(TemporaryKeySchema.DeviceIndex);
            migrationBuilder.Sql(TemporaryKeySchema.TokenIndex);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TemporaryKeys");
        }
    }
}
