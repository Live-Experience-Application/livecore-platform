using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Adds the closed-app Web Push delivery OUTBOX table <c>push_notification_deliveries</c> (CORE-PUSH-002, the
/// "Closed-App Push Notifications" epic, raised by a vertical adopter — ARC-GAP-005, the delivery). Each row is the
/// durable hand-off the API writes after an already-authorized, recipient-filtered session event commits
/// (commit-then-publish, CORE-CONC-002) and the worker drains by sending a CONTENT-FREE push to the recipient's
/// registered subscriptions (docs/10_DATABASE_SCHEMA.md; docs/11_REALTIME_SYNC.md).
///
/// The row carries only IDENTIFIERS — the tenant, the session, the source <c>session_event_id</c> and the recipient
/// <c>user_id</c> — never any projected content (threats T2/T7). Both foreign keys are <c>ON DELETE CASCADE</c>: the
/// <c>organization_id</c> into <c>organizations(id)</c> drops a tenant's pending pushes on tenant teardown
/// (CORE-PRIV-002), and the <c>user_id</c> into <c>users(id)</c> drops a subject's pending pushes on erasure
/// (CORE-PRIV-001) — the same cascade <c>push_subscriptions</c> uses. The <c>session_id</c> and
/// <c>session_event_id</c> are recorded identifiers, not foreign keys (the row is a transient hand-off).
///
/// The <c>Down</c> drops the table, fully reversing the change.
/// </summary>
public partial class AddPushNotificationDeliveries : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "push_notification_deliveries",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                session_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_push_notification_deliveries", x => x.id);
                table.ForeignKey(
                    name: "fk_push_notification_deliveries_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_push_notification_deliveries_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_push_notification_deliveries_organization_id",
            table: "push_notification_deliveries",
            column: "organization_id");

        migrationBuilder.CreateIndex(
            name: "IX_push_notification_deliveries_user_id",
            table: "push_notification_deliveries",
            column: "user_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "push_notification_deliveries");
    }
}
