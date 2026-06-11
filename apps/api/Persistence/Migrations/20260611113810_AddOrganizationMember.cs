using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>organization_members</c> table: organization membership of
/// the Organizations module (CORE-ID-004, csv/database_tables.csv: scope
/// <c>organization</c>, "Tenant membership"). The table is tenant-scoped, so
/// it carries an <c>organization_id</c> foreign key into
/// <c>organizations(id)</c> and a <c>user_id</c> foreign key into
/// <c>users(id)</c> (docs/10_DATABASE_SCHEMA.md: tenant-scoped tables include
/// <c>organization_id</c>; threat T5 in docs/07_SECURITY_THREAT_MODEL.md). The
/// unique index on (<c>organization_id</c>, <c>user_id</c>) enforces at most
/// one membership per subject per organization at the database level, so a
/// second writer can never create a conflicting standing for the same subject
/// in the same tenant; the leading <c>organization_id</c> column also keeps
/// tenant-scoped lookups efficient. Both foreign keys cascade on delete: a
/// membership has no meaning without its organization or its subject. The
/// <c>role</c> column stores the generic membership role name from the
/// authorization matrix (docs/06_AUTHORIZATION_MATRIX.md). Rollback:
/// <see cref="Down"/> drops the table.
/// </summary>
public partial class AddOrganizationMember : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "organization_members",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_organization_members", x => x.id);
                table.ForeignKey(
                    name: "fk_organization_members_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_organization_members_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_organization_members_organization_id_user_id",
            table: "organization_members",
            columns: new[] { "organization_id", "user_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_organization_members_user_id",
            table: "organization_members",
            column: "user_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "organization_members");
    }
}
