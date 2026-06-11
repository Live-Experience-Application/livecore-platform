using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>users</c> table: the user profile reference of the
/// IdentityAccess module (CORE-ID-002, csv/database_tables.csv). The unique
/// index on (issuer, subject_id) enforces one profile per OIDC identity
/// pair at the database level. Rollback: <see cref="Down"/> drops the table.
/// </summary>
public partial class AddUserProfileReference : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "users",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                issuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                subject_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_users", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_users_issuer_subject_id",
            table: "users",
            columns: new[] { "issuer", "subject_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "users");
    }
}
