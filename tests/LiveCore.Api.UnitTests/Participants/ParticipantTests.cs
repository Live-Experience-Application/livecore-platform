using LiveCore.Api.Participants;

namespace LiveCore.Api.UnitTests.Participants;

/// <summary>
/// Unit tests for the <see cref="Participant"/> invariants and its lifecycle
/// (CORE-SES-001): a participant is the generic, session-facing record of a
/// workspace; it is workspace-scoped and tenant-scoped and belongs only to the
/// workspace and organization it names; its user link is OPTIONAL (anonymous and
/// user-linked participants are both valid, docs/03_DOMAIN_LANGUAGE.md); and its
/// status moves from Active to Removed by a soft delete (the "lifecycle" required
/// test, csv/core_epics_stories.csv). The participant carries NO authorization
/// role: it is display identity + lifecycle, not a membership-role assignment
/// (docs/05_MODULE_CONTRACTS.md, docs/06_AUTHORIZATION_MATRIX.md). ToString stays
/// log-safe (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). No vertical-specific
/// actor terms appear anywhere — generic Participant vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv, ADR 0003).
/// </summary>
public class ParticipantTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _foreignOrganizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _foreignWorkspaceId = Guid.CreateVersion7();
    private static readonly Guid _userProfileId = Guid.CreateVersion7();
    private static readonly Guid _foreignUserProfileId = Guid.CreateVersion7();

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Create_for_a_user_copies_organization_workspace_user_and_display_name()
    {
        var participant = Participant.Create(
            _organizationId, _workspaceId, _userProfileId, "Stage Left", _createdAt);

        Assert.NotEqual(Guid.Empty, participant.Id);
        Assert.Equal(_organizationId, participant.OrganizationId);
        Assert.Equal(_workspaceId, participant.WorkspaceId);
        Assert.Equal(_userProfileId, participant.UserProfileId);
        Assert.True(participant.IsLinkedToUser);
        Assert.Equal("Stage Left", participant.DisplayName);
        // A participant starts active (the lifecycle begins at Active).
        Assert.Equal(ParticipantStatus.Active, participant.Status);
        Assert.Equal(_createdAt, participant.CreatedAt);
        Assert.Equal(_createdAt, participant.UpdatedAt);
    }

    [Fact]
    public void Create_for_an_anonymous_participant_leaves_the_user_link_null()
    {
        // docs/03_DOMAIN_LANGUAGE.md: a participant "may be linked to a user". An
        // anonymous/guest participant has no user account, so the link is null
        // and the participant is still fully valid.
        var participant = Participant.Create(
            _organizationId, _workspaceId, userProfileId: null, "Guest 1", _createdAt);

        Assert.Null(participant.UserProfileId);
        Assert.False(participant.IsLinkedToUser);
        Assert.Equal(ParticipantStatus.Active, participant.Status);
    }

    [Fact]
    public void Create_generates_time_ordered_unique_ids()
    {
        var first = Participant.Create(_organizationId, _workspaceId, _userProfileId, "A", _createdAt);
        var second = Participant.Create(_organizationId, _workspaceId, _foreignUserProfileId, "B", _createdAt);

        // UUID version 7 per docs/10_DATABASE_SCHEMA.md (time-ordered ids).
        Assert.Equal(7, first.Id.Version);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Create_trims_the_display_name()
    {
        var participant = Participant.Create(
            _organizationId, _workspaceId, _userProfileId, "  Trimmed Name  ", _createdAt);

        Assert.Equal("Trimmed Name", participant.DisplayName);
    }

    [Fact]
    public void Timestamps_are_normalized_to_utc()
    {
        var localCreatedAt = new DateTimeOffset(2026, 6, 11, 10, 0, 0, TimeSpan.FromHours(2));

        var participant = Participant.Create(
            _organizationId, _workspaceId, _userProfileId, "Name", localCreatedAt);

        Assert.Equal(TimeSpan.Zero, participant.CreatedAt.Offset);
        Assert.Equal(localCreatedAt.ToUniversalTime(), participant.CreatedAt);
    }

    [Fact]
    public void Create_rejects_an_empty_organization_id()
        => Assert.Throws<ArgumentException>(
            () => Participant.Create(Guid.Empty, _workspaceId, _userProfileId, "Name", _createdAt));

    [Fact]
    public void Create_rejects_an_empty_workspace_id()
        => Assert.Throws<ArgumentException>(
            () => Participant.Create(_organizationId, Guid.Empty, _userProfileId, "Name", _createdAt));

    // The user link is optional, but an explicitly empty (not null) user id is
    // neither a valid user nor a valid anonymous participant.
    [Fact]
    public void Create_rejects_an_explicit_empty_user_link()
        => Assert.Throws<ArgumentException>(
            () => Participant.Create(_organizationId, _workspaceId, Guid.Empty, "Name", _createdAt));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_a_blank_display_name(string? displayName)
        => Assert.Throws<ArgumentException>(
            () => Participant.Create(_organizationId, _workspaceId, _userProfileId, displayName!, _createdAt));

    [Fact]
    public void Create_rejects_a_display_name_with_control_characters()
        => Assert.Throws<ArgumentException>(
            () => Participant.Create(_organizationId, _workspaceId, _userProfileId, "bad\tname", _createdAt));

    [Fact]
    public void Create_rejects_a_display_name_over_the_length_bound()
    {
        var tooLong = new string('a', Participant.MaxDisplayNameLength + 1);

        Assert.Throws<ArgumentException>(
            () => Participant.Create(_organizationId, _workspaceId, _userProfileId, tooLong, _createdAt));
    }

    [Fact]
    public void Participant_belongs_only_to_its_own_organization()
    {
        // Negative tenant case (threat T5): a participant belongs only to the
        // organization it names. The foreign organization id, even though it is a
        // real organization, does not match. The organization boundary is checked
        // before the workspace boundary.
        var participant = Participant.Create(
            _organizationId, _workspaceId, _userProfileId, "Name", _createdAt);

        Assert.True(participant.BelongsToOrganization(_organizationId));
        Assert.False(participant.BelongsToOrganization(_foreignOrganizationId));
        Assert.False(participant.BelongsToOrganization(Guid.Empty));
    }

    [Fact]
    public void Participant_belongs_only_to_its_own_workspace()
    {
        // Negative workspace case (threat T5): a participant in workspace W1 is
        // not in workspace W2. The foreign workspace id does not match.
        var participant = Participant.Create(
            _organizationId, _workspaceId, _userProfileId, "Name", _createdAt);

        Assert.True(participant.BelongsToWorkspace(_workspaceId));
        Assert.False(participant.BelongsToWorkspace(_foreignWorkspaceId));
        Assert.False(participant.BelongsToWorkspace(Guid.Empty));
    }

    [Fact]
    public void Belongs_to_subject_matches_only_the_exact_linked_user()
    {
        var participant = Participant.Create(
            _organizationId, _workspaceId, _userProfileId, "Name", _createdAt);

        Assert.True(participant.BelongsToSubject(_userProfileId));
        Assert.False(participant.BelongsToSubject(_foreignUserProfileId));
        Assert.False(participant.BelongsToSubject(Guid.Empty));
    }

    [Fact]
    public void An_anonymous_participant_belongs_to_no_subject()
    {
        var participant = Participant.Create(
            _organizationId, _workspaceId, userProfileId: null, "Guest", _createdAt);

        // An anonymous participant has no user link, so it matches no user id.
        Assert.False(participant.BelongsToSubject(_userProfileId));
        Assert.False(participant.BelongsToSubject(_foreignUserProfileId));
    }

    [Fact]
    public void Identifies_requires_the_exact_organization_workspace_and_id()
    {
        // A participant is the one identified by its id only WITHIN its tenant and
        // workspace: the surrogate id is never honored across a foreign tenant or
        // workspace (threat T1/T5).
        var participant = Participant.Create(
            _organizationId, _workspaceId, _userProfileId, "Name", _createdAt);

        Assert.True(participant.Identifies(_organizationId, _workspaceId, participant.Id));
        Assert.False(participant.Identifies(_foreignOrganizationId, _workspaceId, participant.Id));
        Assert.False(participant.Identifies(_organizationId, _foreignWorkspaceId, participant.Id));
        Assert.False(participant.Identifies(_organizationId, _workspaceId, Guid.CreateVersion7()));
        Assert.False(participant.Identifies(_organizationId, _workspaceId, Guid.Empty));
    }

    [Fact]
    public void Rename_changes_only_the_display_name_and_timestamp()
    {
        var participant = Participant.Create(
            _organizationId, _workspaceId, _userProfileId, "Old Name", _createdAt);

        participant.Rename("New Name", _updatedAt);

        Assert.Equal("New Name", participant.DisplayName);
        Assert.Equal(_updatedAt, participant.UpdatedAt);
        Assert.Equal(_createdAt, participant.CreatedAt);
        // The tenant boundary, the workspace, the user link and the status are
        // immutable across a rename (threat T5).
        Assert.Equal(_organizationId, participant.OrganizationId);
        Assert.Equal(_workspaceId, participant.WorkspaceId);
        Assert.Equal(_userProfileId, participant.UserProfileId);
        Assert.Equal(ParticipantStatus.Active, participant.Status);
    }

    [Fact]
    public void Rename_rejects_a_blank_display_name_and_leaves_the_participant_untouched()
    {
        var participant = Participant.Create(
            _organizationId, _workspaceId, _userProfileId, "Original", _createdAt);

        Assert.Throws<ArgumentException>(() => participant.Rename("   ", _updatedAt));

        Assert.Equal("Original", participant.DisplayName);
        Assert.Equal(_createdAt, participant.UpdatedAt);
    }

    [Fact]
    public void Remove_transitions_active_to_removed_and_updates_the_timestamp()
    {
        // Lifecycle test (required-tests column): the participant's state moves
        // from Active to Removed via the aggregate's Remove method (a soft delete,
        // docs/10_DATABASE_SCHEMA.md). The tenant, workspace and user link never
        // change (threat T5).
        var participant = Participant.Create(
            _organizationId, _workspaceId, _userProfileId, "Name", _createdAt);

        Assert.Equal(ParticipantStatus.Active, participant.Status);

        participant.Remove(_updatedAt);

        Assert.Equal(ParticipantStatus.Removed, participant.Status);
        Assert.Equal(_updatedAt, participant.UpdatedAt);
        Assert.Equal(_createdAt, participant.CreatedAt);
        Assert.Equal(_organizationId, participant.OrganizationId);
        Assert.Equal(_workspaceId, participant.WorkspaceId);
        Assert.Equal(_userProfileId, participant.UserProfileId);
    }

    [Fact]
    public void Remove_is_idempotent_and_does_not_re_stamp_an_already_removed_participant()
    {
        // Removing an already-removed participant is a no-op: it neither errors
        // nor moves the update timestamp forward, so the lifecycle has exactly one
        // removal transition.
        var participant = Participant.Create(
            _organizationId, _workspaceId, _userProfileId, "Name", _createdAt);
        participant.Remove(_updatedAt);

        var laterStamp = _updatedAt.AddHours(1);
        participant.Remove(laterStamp);

        Assert.Equal(ParticipantStatus.Removed, participant.Status);
        // The second removal did not re-stamp the record.
        Assert.Equal(_updatedAt, participant.UpdatedAt);
    }

    [Fact]
    public void A_removed_participant_can_still_be_renamed()
    {
        // Soft delete retains the record, so display-identity metadata can still
        // be corrected on a removed participant without resurrecting it.
        var participant = Participant.Create(
            _organizationId, _workspaceId, _userProfileId, "Name", _createdAt);
        participant.Remove(_updatedAt);

        participant.Rename("Corrected Name", _updatedAt.AddHours(1));

        Assert.Equal(ParticipantStatus.Removed, participant.Status);
        Assert.Equal("Corrected Name", participant.DisplayName);
    }

    [Fact]
    public void IsValidStatus_rejects_an_undefined_status()
    {
        Assert.True(Participant.IsValidStatus(ParticipantStatus.Active));
        Assert.True(Participant.IsValidStatus(ParticipantStatus.Removed));
        Assert.False(Participant.IsValidStatus((ParticipantStatus)999));
    }

    [Fact]
    public void ToString_exposes_identifiers_and_status_but_no_free_form_content()
    {
        // Log-safety (threat T7): structured logs carry identifiers and the
        // lifecycle status, never the display name (free-form metadata content).
        var participant = Participant.Create(
            _organizationId, _workspaceId, _userProfileId, "Secret Display Name", _createdAt);

        var text = participant.ToString();

        Assert.Contains(participant.Id.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_organizationId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_workspaceId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_userProfileId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("Active", text, StringComparison.Ordinal);
        // The display name is never written to logs.
        Assert.DoesNotContain("Secret Display Name", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_marks_an_anonymous_participant_without_leaking_a_user_id()
    {
        var participant = Participant.Create(
            _organizationId, _workspaceId, userProfileId: null, "Guest", _createdAt);

        var text = participant.ToString();

        Assert.Contains("anonymous", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Anonymize_scrubs_the_display_name_and_clears_the_user_link()
    {
        // CORE-PRIV-001 (GDPR Art.17): erasing the linked data subject anonymizes the participant — the
        // display name (PII the SET NULL user foreign key cannot reach) is scrubbed to the fixed placeholder and
        // the user link is cleared, while the record itself survives.
        var participant = Participant.Create(
            _organizationId, _workspaceId, _userProfileId, "Secret Display Name", _createdAt);

        participant.Anonymize(_updatedAt);

        Assert.Equal(Participant.AnonymizedDisplayName, participant.DisplayName);
        Assert.Null(participant.UserProfileId);
        Assert.False(participant.IsLinkedToUser);
        // The record survives (anonymization, not deletion): the boundaries and lifecycle are untouched.
        Assert.Equal(_organizationId, participant.OrganizationId);
        Assert.Equal(_workspaceId, participant.WorkspaceId);
        Assert.Equal(ParticipantStatus.Active, participant.Status);
        Assert.Equal(_updatedAt, participant.UpdatedAt);
        // The placeholder is itself a valid display name and carries no trace of the erased PII.
        Assert.True(Participant.IsValidDisplayName(participant.DisplayName));
        Assert.DoesNotContain("Secret Display Name", participant.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Anonymize_is_idempotent()
    {
        var participant = Participant.Create(
            _organizationId, _workspaceId, _userProfileId, "Secret Display Name", _createdAt);

        participant.Anonymize(_updatedAt);
        participant.Anonymize(_updatedAt);

        Assert.Equal(Participant.AnonymizedDisplayName, participant.DisplayName);
        Assert.Null(participant.UserProfileId);
    }

    [Fact]
    public void Anonymize_normalizes_updated_at_to_utc()
    {
        var participant = Participant.Create(
            _organizationId, _workspaceId, _userProfileId, "Secret Display Name", _createdAt);
        var local = new DateTimeOffset(2026, 6, 12, 11, 0, 0, TimeSpan.FromHours(2));

        participant.Anonymize(local);

        Assert.Equal(TimeSpan.Zero, participant.UpdatedAt.Offset);
        Assert.Equal(local.UtcDateTime, participant.UpdatedAt.UtcDateTime);
    }
}
