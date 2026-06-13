using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Adds the <c>status</c> lifecycle column to the <c>workspaces</c> table: the soft-archive end-state of the
/// Workspaces module (CORE-LIFE-009, the "Resource Lifecycle and Deletion" epic). A workspace previously had
/// create/read/update but no lifecycle end-state; this column records the <see cref="LiveCore.Api.Workspaces.WorkspaceStatus"/>
/// (<c>Active</c> / <c>Archived</c>), persisted by its stable NAME (not a numeric value) exactly like the
/// <c>sessions.status</c> and <c>participants.status</c> columns, so the stored value stays readable and is not
/// coupled to the in-memory storage discriminators.
///
/// The column is NOT NULL with a default of <c>Active</c>, so every existing workspace row is back-filled to the
/// active state (a workspace created before this story was, and remains, active until an owner archives it). New
/// rows are written with their aggregate-supplied status, so the default only governs the back-fill. There is
/// deliberately no dedicated status index: the active-list query already leads with the selective
/// <c>organization_id</c> + membership join, and the status predicate only narrows that result.
///
/// Rollback: <see cref="Down"/> drops the column.
/// </summary>
public partial class AddWorkspaceStatus : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "status",
            table: "workspaces",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "Active");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "status",
            table: "workspaces");
    }
}
