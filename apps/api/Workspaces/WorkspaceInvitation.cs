using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// Workspace invitation aggregate of the Workspaces module (CORE-WS-004:
/// "workspace member invite placeholder with scoped token model").
///
/// A <see cref="WorkspaceInvitation"/> is the Core-owned, product-neutral record
/// that one organization Owner/Admin has invited a target (identified by email,
/// treated purely as data) to join one <see cref="Workspace"/> with one generic
/// <see cref="MembershipRole"/>, gated by a scoped, single-use, time-bound
/// invite token. It is the persistent half of the "invite/add member" route
/// (<c>POST /api/v1/workspaces/{workspaceId}/members</c>, csv/api_routes.csv,
/// module Workspaces, roles Owner,Admin). It is the placeholder deliverable:
/// the invite RECORD plus a securely-modelled scoped token; it deliberately does
/// not model email delivery, notifications or an acceptance UI (those are
/// follow-ups, see the module notes).
///
/// Scoped-token security model (the heart of the story; threat T6 "Invite abuse"
/// in docs/07_SECURITY_THREAT_MODEL.md — "scoped invite tokens, expiry,
/// revocation, role limitation"):
/// <list type="bullet">
///   <item>
///   The token is generated with a cryptographically secure RNG and >= 128 bits
///   of entropy by <see cref="WorkspaceInvitationToken"/>. Only its SHA-256
///   <em>hash</em> (<see cref="TokenHash"/>) is ever stored; the plaintext is
///   returned to the inviter exactly once at creation and is never persisted,
///   never re-derivable and never logged (threats T6/T7). This aggregate never
///   accepts or holds the plaintext.
///   </item>
///   <item>
///   The token is SCOPED: it is bound to exactly one organization
///   (<see cref="OrganizationId"/>), one workspace (<see cref="WorkspaceId"/>)
///   and one role (<see cref="Role"/>), and it expires
///   (<see cref="ExpiresAt"/>). Redemption re-checks this scope server-side; the
///   secret encodes nothing trusted.
///   </item>
///   <item>
///   The token is SINGLE-USE and revocable: <see cref="Status"/> moves from
///   <see cref="WorkspaceInvitationStatus.Pending"/> to
///   <see cref="WorkspaceInvitationStatus.Accepted"/> on redemption or
///   <see cref="WorkspaceInvitationStatus.Revoked"/> on revocation, and a
///   non-pending or expired invitation can never be redeemed (threat T6). The
///   <see cref="Redeem"/> method exists for model completeness; there is no HTTP
///   acceptance route in this placeholder story.
///   </item>
/// </list>
/// The token is NOT authentication: it never logs anyone in and is not a
/// password (docs/adr/0005-oidc-first-authentication.md). It is a one-time
/// scoped grant to be admitted to a workspace, distinct from the OIDC token the
/// inviter authenticates with.
///
/// Tenant boundary: the invitation is workspace-scoped and tenant-scoped, so it
/// carries both <see cref="OrganizationId"/> (the tenant; checked before the
/// workspace boundary) and <see cref="WorkspaceId"/>, mirroring
/// <see cref="WorkspaceMember"/> (docs/10_DATABASE_SCHEMA.md: tenant-scoped
/// tables include <c>organization_id</c>, workspace-scoped tables include
/// <c>workspace_id</c>; threat T5). An invitation grants standing only in the
/// workspace it names; the surrogate <see cref="Id"/> and the token hash are
/// only ever honoured inside this organization and workspace (threats T1/T5).
///
/// Target invariant: <see cref="InvitedEmail"/> is the invitee identifier,
/// validated for shape but treated strictly as DATA, never as a credential and
/// never as an authorization input. The platform implements no custom password
/// auth (docs/adr/0005); the email merely records who the invite is for. It is
/// excluded from the log-safe <see cref="ToString"/> as personal data (threat
/// T7), alongside the token hash.
/// </summary>
public sealed class WorkspaceInvitation
{
    /// <summary>
    /// Default validity window of a new invitation. Bounding the lifetime keeps
    /// a leaked token from being redeemable indefinitely (threat T6 expiry
    /// control). Seven days is a conservative default for a manual invite flow.
    /// </summary>
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromDays(7);

    /// <summary>
    /// Generic, PII-free placeholder the invited email is replaced with when the invitee is erased as a data
    /// subject (<see cref="AnonymizeInvitedEmail"/>, CORE-PRIV-001). It is a fixed, non-routable address under
    /// the reserved <c>.invalid</c> TLD (RFC 2606), so no trace of the erased subject's email survives while the
    /// invitation row is preserved. It is a valid invited email (<see cref="IsValidInvitedEmail"/>).
    /// </summary>
    public const string AnonymizedInvitedEmail = "erased@erased.invalid";

    private WorkspaceInvitation(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        string invitedEmail,
        MembershipRole role,
        string tokenHash,
        WorkspaceInvitationStatus status,
        DateTimeOffset expiresAt,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Invitation id must not be empty.", nameof(id));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (!IsValidInvitedEmail(invitedEmail))
        {
            throw new ArgumentException("Invited email violates the email invariants.", nameof(invitedEmail));
        }

        if (!IsValidRole(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Role is not a defined membership role.");
        }

        if (!WorkspaceInvitationToken.IsValidHash(tokenHash))
        {
            throw new ArgumentException("Token hash violates the hash invariants.", nameof(tokenHash));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Status is not a defined invitation status.");
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "An invitation cannot be updated before it was created.",
                nameof(updatedAt));
        }

        if (expiresAt <= createdAt)
        {
            throw new ArgumentException(
                "An invitation must expire after it was created.",
                nameof(expiresAt));
        }

        Id = id;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        InvitedEmail = invitedEmail;
        Role = role;
        TokenHash = tokenHash;
        Status = status;
        // Timestamps are normalized to UTC so the persisted timestamptz values
        // are offset-independent (docs/10_DATABASE_SCHEMA.md).
        ExpiresAt = expiresAt.ToUniversalTime();
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private WorkspaceInvitation()
    {
        InvitedEmail = null!;
        TokenHash = null!;
    }

    /// <summary>
    /// Surrogate key of the invitation row (UUID version 7, time-ordered per
    /// docs/10_DATABASE_SCHEMA.md). On its own it never authorizes anything:
    /// every lookup is scoped by <see cref="OrganizationId"/> and
    /// <see cref="WorkspaceId"/> (threats T1/T5).
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Tenant boundary of the invitation: the id of the organization that owns
    /// the workspace this invitation is for (the <c>organization_id</c> foreign
    /// key to <c>organizations</c>). Immutable; an invitation never crosses to
    /// another organization (threat T5).
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Workspace boundary of the invitation: the id of the workspace the invite
    /// grants admission to (the <c>workspace_id</c> foreign key to
    /// <c>workspaces</c>). Immutable; an invitation never moves to another
    /// workspace (threat T5).
    /// </summary>
    public Guid WorkspaceId { get; }

    /// <summary>
    /// Email of the invitee. Validated for shape, but DATA only: never a
    /// credential and never an authorization input (docs/adr/0005). Excluded
    /// from <see cref="ToString"/> as personal data (threat T7). The only
    /// mutation is the data-subject erasure (<see cref="AnonymizeInvitedEmail"/>,
    /// CORE-PRIV-001), which scrubs it to the PII-free
    /// <see cref="AnonymizedInvitedEmail"/> placeholder.
    /// </summary>
    public string InvitedEmail { get; private set; }

    /// <summary>
    /// Generic role the invitee will hold once the invite is redeemed,
    /// drawn from the authorization matrix (docs/06_AUTHORIZATION_MATRIX.md).
    /// The role scopes the token: redemption grants exactly this role and no
    /// other (threat T6 role limitation).
    /// </summary>
    public MembershipRole Role { get; }

    /// <summary>
    /// SHA-256 hash (base64) of the scoped invite token. This is the ONLY
    /// representation of the secret stored at rest: the plaintext is never
    /// persisted, never re-derivable from this hash and never logged (threats
    /// T6/T7). A presented token is matched by hashing it and comparing against
    /// this value with a constant-time comparison
    /// (<see cref="WorkspaceInvitationToken.HashesEqual"/>).
    /// </summary>
    public string TokenHash { get; }

    /// <summary>
    /// Lifecycle status of the invitation. Together with <see cref="ExpiresAt"/>
    /// it gates redemption: only a pending, unexpired invitation is redeemable
    /// (threat T6 single-use/expiry/revocation).
    /// </summary>
    public WorkspaceInvitationStatus Status { get; private set; }

    /// <summary>When the scoped invite token expires (UTC); threat T6 expiry.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>When this invitation was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this invitation was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a new pending invitation for the given workspace (owned by the
    /// given organization) with the given target email and granted role, sealed
    /// by the SHA-256 hash of a freshly generated scoped token. The PLAINTEXT
    /// token is generated and returned through <paramref name="plaintextToken"/>
    /// exactly once for the caller to hand to the inviter; it is never stored on
    /// the aggregate (only its hash is), so it can never be read back or logged
    /// later (threats T6/T7).
    /// </summary>
    /// <param name="organizationId">The owning tenant of the workspace.</param>
    /// <param name="workspaceId">The workspace the invite grants admission to.</param>
    /// <param name="invitedEmail">The invitee's email (data, not a credential).</param>
    /// <param name="role">The generic role the invite will grant on redemption.</param>
    /// <param name="createdAt">Creation timestamp; the validity window is added to it.</param>
    /// <param name="plaintextToken">
    /// Receives the one-time plaintext token. The caller returns it to the
    /// inviter once and must never persist or log it.
    /// </param>
    /// <param name="validity">
    /// Optional validity window; defaults to <see cref="DefaultValidity"/>. Must
    /// be positive.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The organization id or workspace id is empty, the email is invalid, or
    /// the validity window is not positive.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The role is not a defined <see cref="MembershipRole"/>.
    /// </exception>
    public static WorkspaceInvitation Create(
        Guid organizationId,
        Guid workspaceId,
        string invitedEmail,
        MembershipRole role,
        DateTimeOffset createdAt,
        out string plaintextToken,
        TimeSpan? validity = null)
    {
        var window = validity ?? DefaultValidity;
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentException("The validity window must be positive.", nameof(validity));
        }

        // Generate the secret and immediately reduce it to its stored hash. The
        // plaintext leaves only through the out parameter; the aggregate keeps
        // the hash alone, so the secret is returned exactly once and never lives
        // at rest (threats T6/T7).
        var token = WorkspaceInvitationToken.Generate();
        var tokenHash = WorkspaceInvitationToken.Hash(token);

        var invitation = new WorkspaceInvitation(
            // UUIDv7 id derived from the creation time so ordering by id
            // (ListPendingByWorkspaceAsync) is chronological even for invitations
            // created in the same millisecond, matching every other aggregate.
            Guid.CreateVersion7(createdAt),
            organizationId,
            workspaceId,
            NormalizeEmail(invitedEmail),
            role,
            tokenHash,
            WorkspaceInvitationStatus.Pending,
            createdAt + window,
            createdAt,
            createdAt);

        plaintextToken = token;
        return invitation;
    }

    /// <summary>
    /// Whether this invitation belongs to exactly the given organization. Empty
    /// ids match nothing. The tenant-boundary check, checked before the
    /// workspace boundary (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this invitation belongs to exactly the given workspace. Empty ids
    /// match nothing. The workspace-boundary check (threat T5).
    /// </summary>
    public bool BelongsToWorkspace(Guid workspaceId)
        => workspaceId != Guid.Empty && WorkspaceId == workspaceId;

    /// <summary>
    /// Whether the scoped token may still be redeemed at the given instant: the
    /// invitation is pending and not yet expired. A non-pending (accepted or
    /// revoked) or expired invitation is never redeemable (threat T6: single-use,
    /// expiry, revocation).
    /// </summary>
    public bool IsRedeemable(DateTimeOffset asOf)
        => Status == WorkspaceInvitationStatus.Pending && asOf < ExpiresAt;

    /// <summary>
    /// Whether the given presented plaintext token matches this invitation's
    /// stored hash. The presented token is hashed and compared with the stored
    /// hash in constant time; the stored hash is never reversed and the
    /// comparison does not leak how much of a guess was correct (threat T6).
    /// This is a pure match check: it does not consider status or expiry, which
    /// <see cref="IsRedeemable"/> and <see cref="Redeem"/> enforce.
    /// </summary>
    public bool MatchesToken(string presentedToken)
    {
        if (string.IsNullOrWhiteSpace(presentedToken))
        {
            return false;
        }

        var presentedHash = WorkspaceInvitationToken.Hash(presentedToken);
        return WorkspaceInvitationToken.HashesEqual(TokenHash, presentedHash);
    }

    /// <summary>
    /// Marks the invitation redeemed (single-use). Provided for model
    /// completeness; this placeholder story exposes no HTTP acceptance route, so
    /// the redeem/accept flow that would call this is a follow-up. Redemption is
    /// only valid while the invitation is pending and unexpired, so a token can
    /// be used at most once and never after it has expired or been revoked
    /// (threat T6).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The invitation is not redeemable (already accepted, revoked, or expired).
    /// </exception>
    public void Redeem(DateTimeOffset redeemedAt)
    {
        if (!IsRedeemable(redeemedAt))
        {
            throw new InvalidOperationException(
                "The invitation is not redeemable: it is not pending or it has expired.");
        }

        Status = WorkspaceInvitationStatus.Accepted;
        UpdatedAt = redeemedAt.ToUniversalTime();
    }

    /// <summary>
    /// Revokes the invitation so its token can never be redeemed (threat T6
    /// revocation control). Provided for model completeness; no HTTP route calls
    /// it in this placeholder story. Only a pending invitation may be revoked;
    /// revoking an already-redeemed invitation is rejected so an accepted
    /// membership is never silently undone here.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The invitation is not pending (already accepted or revoked).
    /// </exception>
    public void Revoke(DateTimeOffset revokedAt)
    {
        if (Status != WorkspaceInvitationStatus.Pending)
        {
            throw new InvalidOperationException(
                "Only a pending invitation can be revoked.");
        }

        Status = WorkspaceInvitationStatus.Revoked;
        UpdatedAt = revokedAt.ToUniversalTime();
    }

    /// <summary>
    /// Anonymizes the invited email for a data-subject erasure (CORE-PRIV-001, GDPR Art.17): replaces the
    /// invitee identifier with the fixed, non-routable PII-free <see cref="AnonymizedInvitedEmail"/> placeholder
    /// so no trace of the erased subject's email survives. The invitation row itself is PRESERVED — this is
    /// anonymization, not deletion — so the tenant, workspace, role, token hash, lifecycle
    /// <see cref="Status"/> and expiry are all untouched; only the personal data the erasure must remove (the
    /// plaintext email, which no foreign key references) is scrubbed. The operation is idempotent: re-anonymizing
    /// re-applies the same placeholder.
    /// </summary>
    public void AnonymizeInvitedEmail(DateTimeOffset updatedAt)
    {
        InvitedEmail = AnonymizedInvitedEmail;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether the given value is a valid invited email: non-blank, within the
    /// length bound, control-character free and of the basic local@domain shape.
    /// Reuses the IdentityAccess shape check so Core validates the email the same
    /// way everywhere; the value is still data, not a credential.
    /// </summary>
    public static bool IsValidInvitedEmail(string? value)
        => OidcPrincipal.IsValidEmail(value);

    /// <summary>
    /// Whether the given value is a defined <see cref="MembershipRole"/>. Rejects
    /// an undefined enum value a cast could otherwise smuggle in, so an
    /// invitation can never grant an undefined role (threat T6 role limitation).
    /// </summary>
    public static bool IsValidRole(MembershipRole role) => Enum.IsDefined(role);

    // Normalize the email to a trimmed form for storage. Casing is preserved
    // because the local part of an address is technically case-sensitive; the
    // value is informational data, not a key, so no further canonicalization is
    // applied.
    private static string NormalizeEmail(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>
    /// Identifier-only representation that is safe for structured logs:
    /// invitation id, organization id, workspace id, granted role, status and
    /// expiry. The token hash and the invited email are DELIBERATELY excluded —
    /// the hash is a credential-derived secret and the email is personal data,
    /// neither of which may appear in logs (threats T6/T7 in
    /// docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"WorkspaceInvitation {Id} org={OrganizationId} ws={WorkspaceId} role={Role} status={Status} expiresAt={ExpiresAt:O}";
}
