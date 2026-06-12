using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the <c>templates</c> table: the versioned template REGISTRY of the Templates module
/// (CORE-ENT-004, fourth story of the "Entity System and Templates" epic; the Templates module's
/// first table; csv/database_tables.csv row 19: module Templates, scope <c>global/organization</c>,
/// "Template registry"). A template stores a versioned template <c>definition</c> document that the
/// loader materializes a workspace's entity types from.
///
/// Scope (the "global/organization" scope) is modelled by a single NULLABLE <c>organization_id</c>
/// column that is a foreign key into <c>organizations(id)</c>: <c>NULL</c> is a GLOBAL template
/// (available to every tenant), a set value is an ORGANIZATION-scoped template owned by one tenant
/// (docs/10_DATABASE_SCHEMA.md principle: tenant-scoped tables include <c>organization_id</c>;
/// threat T5 in docs/07_SECURITY_THREAT_MODEL.md). The nullable FK cascades on delete: an
/// organization-scoped template has no meaning without its owning tenant, so removing the
/// organization removes its templates; a global template has no organization and is unaffected.
///
/// The <c>template_key</c> column stores the canonical dot-separated natural key (the DATA a
/// vertical supplies; Core never branches on its value — the template boundary, docs/04);
/// <c>version</c> is the positive integer template version (template versioning,
/// docs/05_MODULE_CONTRACTS.md); <c>definition</c> stores the template content JSON document as
/// plain text (validated for JSON well-formedness + minimal structure only, never against a higher
/// schema or for entity-instance conformance).
///
/// docs/10_DATABASE_SCHEMA.md documents no critical index for <c>templates</c>, so the indexes
/// model the per-scope natural key. Because SQLite (the in-memory provider the tests use) treats
/// NULL values as DISTINCT in a unique index, two FILTERED partial unique indexes enforce the two
/// scope halves separately: the unique index on (<c>template_key</c>, <c>version</c>) WHERE
/// <c>organization_id IS NULL</c> is the GLOBAL per-scope key (no two global templates share a
/// key+version); the unique index on (<c>organization_id</c>, <c>template_key</c>, <c>version</c>)
/// WHERE <c>organization_id IS NOT NULL</c> is the ORGANIZATION per-scope key (no two templates of
/// one organization share a key+version, while a different organization or the global pool may
/// reuse the same key+version; threat T5). The org-scoped filtered unique index also serves the
/// per-tenant key lookup, and the global filtered unique index the global key lookup. The filter
/// predicates are standard SQL understood by both PostgreSQL and the SQLite provider, exactly like
/// the filtered unique index on <c>participants(workspace_id, user_id)</c>. Rollback:
/// <see cref="Down"/> drops the table.
/// </summary>
public partial class AddTemplate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "templates",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                template_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                version = table.Column<int>(type: "integer", nullable: false),
                definition = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_templates", x => x.id);
                table.ForeignKey(
                    name: "fk_templates_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_templates_global_template_key_version",
            table: "templates",
            columns: new[] { "template_key", "version" },
            unique: true,
            filter: "\"organization_id\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "ix_templates_organization_template_key_version",
            table: "templates",
            columns: new[] { "organization_id", "template_key", "version" },
            unique: true,
            filter: "\"organization_id\" IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "templates");
    }
}
