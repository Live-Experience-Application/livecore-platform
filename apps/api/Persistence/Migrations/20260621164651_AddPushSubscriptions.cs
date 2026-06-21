using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Adds the per-principal Web Push subscription table <c>push_subscriptions</c> (CORE-PUSH-001, the
/// "Closed-App Push Notifications" epic, raised by a vertical adopter — ARC-GAP-005, the enabler). It backs the
/// closed-app push registration surface (<c>POST</c>/<c>DELETE /api/v1/me/push-subscriptions</c>): each row is
/// the W3C Push API subscription a browser produced for one principal — the push service <c>endpoint</c> URL
/// plus the client's <c>p256dh</c> public key and <c>auth</c> secret (docs/10_DATABASE_SCHEMA.md).
///
/// The table is GLOBAL scope: a subscription is per-principal personal data keyed by the <c>users(id)</c>
/// profile, not tenant data, so it carries no <c>organization_id</c> (exactly like the <c>users</c> table it
/// hangs off). The unique index on (<c>user_id</c>, <c>endpoint</c>) makes a re-registration of the same browser
/// endpoint update the existing row's keys rather than create a duplicate, and backs the per-principal
/// list/delete lookups (threats T1/T5). The <c>user_id</c> foreign key into <c>users(id)</c> is
/// <c>ON DELETE CASCADE</c>, so the data-subject erasure (CORE-PRIV-001) removes a subject's subscriptions
/// automatically — the same cascade the org/workspace membership tables use.
///
/// The <c>Down</c> drops the table, fully reversing the change.
/// </summary>
public partial class AddPushSubscriptions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "push_subscriptions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                endpoint = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                p256dh = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                auth = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_push_subscriptions", x => x.id);
                table.ForeignKey(
                    name: "fk_push_subscriptions_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_push_subscriptions_user_id_endpoint",
            table: "push_subscriptions",
            columns: new[] { "user_id", "endpoint" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "push_subscriptions");
    }
}
