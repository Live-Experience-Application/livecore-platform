// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;

namespace LiveCore.Api.UnitTests.Workspaces;

/// <summary>
/// Unit tests for the <see cref="WorkspaceInvitation"/> aggregate and its scoped
/// token model (CORE-WS-004). The security-critical properties proven here are
/// the scoped-token invariants (threat T6 "Invite abuse" and threat T7 "Log
/// leaks" in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>the plaintext token is returned exactly once at creation and is never
///   stored on the aggregate (only its SHA-256 hash is);</item>
///   <item>the stored hash is not the plaintext and is not reversible to it;</item>
///   <item>the token is single-use (redeem moves to Accepted and cannot repeat),
///   expires, and is revocable;</item>
///   <item>the invite is scoped to one organization, workspace and role, and an
///   undefined role is rejected (role limitation);</item>
///   <item>the invited email is data, not a credential, validated for shape;</item>
///   <item>ToString is log-safe: it never contains the token hash or the email.</item>
/// </list>
/// </summary>
public class WorkspaceInvitationTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private const string _email = "invitee@example.test";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_copies_scope_role_and_status_and_returns_a_one_time_token()
    {
        var invitation = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Admin, _createdAt, out var token);

        Assert.NotEqual(Guid.Empty, invitation.Id);
        Assert.Equal(_organizationId, invitation.OrganizationId);
        Assert.Equal(_workspaceId, invitation.WorkspaceId);
        Assert.Equal(_email, invitation.InvitedEmail);
        Assert.Equal(MembershipRole.Admin, invitation.Role);
        Assert.Equal(WorkspaceInvitationStatus.Pending, invitation.Status);
        Assert.Equal(_createdAt, invitation.CreatedAt);
        Assert.Equal(_createdAt, invitation.UpdatedAt);
        Assert.Equal(_createdAt + WorkspaceInvitation.DefaultValidity, invitation.ExpiresAt);

        // A non-empty plaintext token is handed back exactly once.
        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public void Create_generates_a_time_ordered_uuid_v7_id()
    {
        var invitation = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Host, _createdAt, out _);

        // UUID version 7 per docs/10_DATABASE_SCHEMA.md (time-ordered ids).
        Assert.Equal(7, invitation.Id.Version);
    }

    [Fact]
    public void The_plaintext_token_is_never_stored_on_the_aggregate()
    {
        // T6/T7: the aggregate keeps only the SHA-256 HASH; the plaintext is
        // returned once and never persisted. The stored hash must equal the hash
        // of the plaintext (so a presented token can be matched) but must not
        // equal the plaintext (so the secret is not at rest).
        var invitation = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Owner, _createdAt, out var token);

        Assert.NotEqual(token, invitation.TokenHash);
        Assert.Equal(WorkspaceInvitationToken.Hash(token), invitation.TokenHash);
        // The hash is a base64 SHA-256 digest, not the base64url plaintext.
        Assert.True(WorkspaceInvitationToken.IsValidHash(invitation.TokenHash));
    }

    [Fact]
    public void Two_invitations_get_distinct_tokens_and_distinct_hashes()
    {
        // The RNG yields a fresh secret per invitation, so two invites never
        // share a token or a stored hash (threat T6).
        var first = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Owner, _createdAt, out var firstToken);
        var second = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Owner, _createdAt, out var secondToken);

        Assert.NotEqual(firstToken, secondToken);
        Assert.NotEqual(first.TokenHash, second.TokenHash);
    }

    [Fact]
    public void MatchesToken_is_true_only_for_the_exact_plaintext()
    {
        var invitation = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Owner, _createdAt, out var token);

        Assert.True(invitation.MatchesToken(token));
        Assert.False(invitation.MatchesToken(token + "x"));
        Assert.False(invitation.MatchesToken("not-the-token"));
        Assert.False(invitation.MatchesToken(""));
        Assert.False(invitation.MatchesToken(" "));
    }

    [Fact]
    public void Timestamps_are_normalized_to_utc()
    {
        var localCreatedAt = new DateTimeOffset(2026, 6, 11, 10, 0, 0, TimeSpan.FromHours(2));

        var invitation = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Host, localCreatedAt, out _);

        Assert.Equal(TimeSpan.Zero, invitation.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, invitation.ExpiresAt.Offset);
        Assert.Equal(localCreatedAt.ToUniversalTime(), invitation.CreatedAt);
    }

    [Fact]
    public void Create_honors_a_custom_validity_window()
    {
        var validity = TimeSpan.FromHours(2);

        var invitation = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Host, _createdAt, out _, validity);

        Assert.Equal(_createdAt + validity, invitation.ExpiresAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_rejects_a_non_positive_validity_window(int hours)
        => Assert.Throws<ArgumentException>(() => WorkspaceInvitation.Create(
            _organizationId,
            _workspaceId,
            _email,
            MembershipRole.Host,
            _createdAt,
            out _,
            TimeSpan.FromHours(hours)));

    [Fact]
    public void Create_rejects_an_empty_organization_id()
        => Assert.Throws<ArgumentException>(() => WorkspaceInvitation.Create(
            Guid.Empty, _workspaceId, _email, MembershipRole.Host, _createdAt, out _));

    [Fact]
    public void Create_rejects_an_empty_workspace_id()
        => Assert.Throws<ArgumentException>(() => WorkspaceInvitation.Create(
            _organizationId, Guid.Empty, _email, MembershipRole.Host, _createdAt, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("@no-local")]
    [InlineData("no-domain@")]
    public void Create_rejects_an_invalid_invited_email(string? email)
        => Assert.Throws<ArgumentException>(() => WorkspaceInvitation.Create(
            _organizationId, _workspaceId, email!, MembershipRole.Host, _createdAt, out _));

    [Fact]
    public void Create_rejects_an_undefined_role()
        => Assert.Throws<ArgumentOutOfRangeException>(() => WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, (MembershipRole)999, _createdAt, out _));

    // ---- single-use, expiry, revocation (threat T6) -------------------------

    [Fact]
    public void A_fresh_invitation_is_redeemable_before_expiry()
    {
        var invitation = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Host, _createdAt, out _);

        Assert.True(invitation.IsRedeemable(_createdAt));
        Assert.True(invitation.IsRedeemable(invitation.ExpiresAt - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void An_invitation_is_not_redeemable_at_or_after_expiry()
    {
        var invitation = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Host, _createdAt, out _);

        Assert.False(invitation.IsRedeemable(invitation.ExpiresAt));
        Assert.False(invitation.IsRedeemable(invitation.ExpiresAt + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Redeem_marks_the_invitation_accepted_and_is_single_use()
    {
        var invitation = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Host, _createdAt, out _);
        var redeemedAt = _createdAt + TimeSpan.FromMinutes(5);

        invitation.Redeem(redeemedAt);

        Assert.Equal(WorkspaceInvitationStatus.Accepted, invitation.Status);
        Assert.Equal(redeemedAt, invitation.UpdatedAt);
        Assert.False(invitation.IsRedeemable(redeemedAt));
        // Single-use: a second redemption is rejected.
        Assert.Throws<InvalidOperationException>(() => invitation.Redeem(redeemedAt));
    }

    [Fact]
    public void Redeem_after_expiry_is_rejected()
    {
        var invitation = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Host, _createdAt, out _);

        Assert.Throws<InvalidOperationException>(
            () => invitation.Redeem(invitation.ExpiresAt + TimeSpan.FromSeconds(1)));
        // The status is untouched: an expired invite was never accepted.
        Assert.Equal(WorkspaceInvitationStatus.Pending, invitation.Status);
    }

    [Fact]
    public void Revoke_blocks_future_redemption()
    {
        var invitation = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Host, _createdAt, out _);
        var revokedAt = _createdAt + TimeSpan.FromMinutes(1);

        invitation.Revoke(revokedAt);

        Assert.Equal(WorkspaceInvitationStatus.Revoked, invitation.Status);
        Assert.False(invitation.IsRedeemable(revokedAt));
        Assert.Throws<InvalidOperationException>(() => invitation.Redeem(revokedAt));
    }

    [Fact]
    public void Revoke_after_acceptance_is_rejected()
    {
        var invitation = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Host, _createdAt, out _);
        invitation.Redeem(_createdAt + TimeSpan.FromMinutes(1));

        // An accepted membership is not silently undone by revoke here.
        Assert.Throws<InvalidOperationException>(
            () => invitation.Revoke(_createdAt + TimeSpan.FromMinutes(2)));
        Assert.Equal(WorkspaceInvitationStatus.Accepted, invitation.Status);
    }

    // ---- log-safety (threat T7) ---------------------------------------------

    [Fact]
    public void ToString_is_log_safe_and_excludes_the_token_hash_and_email()
    {
        var invitation = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Admin, _createdAt, out var token);

        var text = invitation.ToString();

        // The credential-derived hash and the personal email never appear in a
        // log-safe representation (threats T6/T7).
        Assert.DoesNotContain(invitation.TokenHash, text, StringComparison.Ordinal);
        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(_email, text, StringComparison.Ordinal);
        // It carries identifiers and authorization metadata only.
        Assert.Contains(invitation.Id.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_organizationId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_workspaceId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("role=Admin", text, StringComparison.Ordinal);
        Assert.Contains("status=Pending", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnonymizeInvitedEmail_scrubs_the_email_and_preserves_everything_else()
    {
        // CORE-PRIV-001 (GDPR Art.17): erasing the invitee anonymizes the invited email (PII no foreign key
        // references) to the fixed non-routable placeholder while the invitation row is preserved.
        var invitation = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Admin, _createdAt, out _);
        var updatedAt = _createdAt.AddHours(1);

        invitation.AnonymizeInvitedEmail(updatedAt);

        Assert.Equal(WorkspaceInvitation.AnonymizedInvitedEmail, invitation.InvitedEmail);
        Assert.NotEqual(_email, invitation.InvitedEmail);
        // The placeholder is a valid invited email and carries no trace of the erased address.
        Assert.True(WorkspaceInvitation.IsValidInvitedEmail(invitation.InvitedEmail));
        // The invitation survives (anonymization, not deletion): scope, role, token hash and lifecycle hold.
        Assert.Equal(_organizationId, invitation.OrganizationId);
        Assert.Equal(_workspaceId, invitation.WorkspaceId);
        Assert.Equal(MembershipRole.Admin, invitation.Role);
        Assert.Equal(WorkspaceInvitationStatus.Pending, invitation.Status);
        Assert.Equal(updatedAt, invitation.UpdatedAt);
        Assert.DoesNotContain(_email, invitation.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnonymizeInvitedEmail_normalizes_updated_at_to_utc()
    {
        var invitation = WorkspaceInvitation.Create(
            _organizationId, _workspaceId, _email, MembershipRole.Admin, _createdAt, out _);
        var local = new DateTimeOffset(2026, 6, 12, 11, 0, 0, TimeSpan.FromHours(2));

        invitation.AnonymizeInvitedEmail(local);

        Assert.Equal(TimeSpan.Zero, invitation.UpdatedAt.Offset);
        Assert.Equal(local.UtcDateTime, invitation.UpdatedAt.UtcDateTime);
    }
}
