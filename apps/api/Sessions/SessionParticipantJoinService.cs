using LiveCore.Api.Entitlements;
using LiveCore.Api.Participants;
using LiveCore.Api.Realtime;

namespace LiveCore.Api.Sessions;

/// <summary>
/// Session participant join service of the Sessions module (CORE-SES-003).
///
/// This is the single fail-closed decision of whether a participant may join a
/// session: it turns an (organization, workspace, session, participant) request
/// into either an admission (<see cref="SessionJoinResult"/> carrying the validated
/// ids) or a typed denial. It is the exact precedent of CORE-ID-005's
/// <c>TenantContextResolver</c> — a plain service over repositories that returns a
/// trusted result or a fail-closed denial — applied to the session-join boundary.
///
/// The Sessions module naturally depends on Participants (a session admits
/// participants), so this service consumes the <see cref="ISessionRepository"/> and
/// the <see cref="IParticipantRepository"/> through their explicit module contracts.
/// That is the allowed cross-module access (docs/02_ARCHITECTURE.md; docs/05_MODULE_CONTRACTS.md:
/// modules talk through contracts, not each other's tables), not direct table
/// access.
///
/// Join rule (defence in depth, threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md):
/// a participant may join a session only when ALL of the following hold, checked in
/// order and denying at the first failure, each denial hiding existence so nothing
/// outside the caller's tenant/workspace is ever leaked:
/// <list type="number">
///   <item>a session with the requested id exists WITHIN the requested organization
///   and workspace — a session in another organization or workspace is reported as
///   <see cref="SessionJoinDenialReason.SessionNotFound"/>, never leaked
///   (docs/06_AUTHORIZATION_MATRIX.md: the organization boundary is checked before
///   the workspace boundary; the repository lookup enforces both);</item>
///   <item>a participant with the requested id exists WITHIN the same organization
///   and workspace — a participant in another organization or workspace is reported
///   as <see cref="SessionJoinDenialReason.ParticipantNotFound"/>, never leaked;</item>
///   <item>the loaded session and participant both re-assert that they belong to
///   exactly the requested (organization, workspace, id) — a belt-and-suspenders
///   re-validation mirroring <c>TenantContext.Create</c>, so an admission can never
///   be minted from mismatched inputs;</item>
///   <item>the participant holds standing: its status is
///   <see cref="ParticipantStatus.Active"/>; a removed participant is denied with
///   <see cref="SessionJoinDenialReason.ParticipantNotActive"/> (CORE-SES-001);</item>
///   <item>the session is joinable: its status is
///   <see cref="SessionStatus.Prepared"/> or <see cref="SessionStatus.Live"/>; an
///   ended session is denied with
///   <see cref="SessionJoinDenialReason.SessionNotJoinable"/> (CORE-SES-002);</item>
///   <item>the session is below its plan participant limit: admitting one more
///   participant must not exceed the server-enforced <c>session.participant.max</c>
///   quota; a session already at its cap is denied with
///   <see cref="SessionJoinDenialReason.QuotaExceeded"/> (CORE-MON-005). This last
///   gate is the only one that CONSUMES — it atomically check-and-consumes one unit of
///   the session's participant quota through <see cref="QuotaEnforcementService"/>
///   (CORE-CONC-004), so it runs only after every cheaper existence / isolation /
///   lifecycle gate has passed and a unit is reserved only for a join that is admitted;
///   a denial for any earlier reason consumes nothing.</item>
/// </list>
///
/// THE FREE-TIER PARTICIPANT GATE (CORE-MON-005). The participant cap is enforced
/// HERE, server-side, not in the UI: the join path itself rejects a participant once
/// the session is at its <c>session.participant.max</c> limit, so the documented
/// free-tier cap cannot be bypassed by a client that simply does not render the
/// paywall (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md: "Free limits cannot be
/// bypassed by clients"). The quota subject is the SESSION
/// (<see cref="EntitlementSubjectType.Session"/>), so usage is counted per session,
/// and the limit is whatever the session is granted — a free session's small cap or a
/// paid plan's larger / unlimited (fair-use) grant. Core enforces only a quota that
/// EXISTS: when no <c>session.participant.max</c> quota governs the deployment the join
/// is ungoverned and proceeds (a deployment that wants the free cap defines the quota
/// and grants every session the free entitlement, the grant-chain / presence wiring).
/// Because the check-and-consume is a single atomic limit-guarded statement
/// (CORE-CONC-004), concurrent joins can never overrun the cap: N joins racing for the
/// last slot yield exactly one admission and N-1 quota-exceeded. The symmetric release
/// (a leaving participant frees a slot) is <see cref="SessionParticipantLeaveService"/>,
/// so the counter reflects the session's CURRENT participants rather than a lifetime
/// total — exactly as <c>session.active.max</c> is consumed at start and released at end.
///
/// This service computes the security decision AND, when (and only when) it admits a
/// join, emits the durable <c>ParticipantJoined</c> session event (CORE-EVT-002). The
/// event is published through the reused <see cref="ISessionEventPublisher"/> (the
/// same seam the reveal and start/end commands use), so the Realtime module stays the
/// sole owner of delivery and the anti-leak recipient resolver is not duplicated. The
/// event is a SUBJECTLESS audience event carrying the participant IDENTIFIER only (never
/// a display name or any other PII, threat T7; see <see cref="ParticipantPresenceEvent"/>),
/// so it is delivered to the session hosts (always — host-visible), the observers and
/// every active participant. A DENIAL emits nothing: a participant outside the caller's
/// tenant/workspace, a removed participant or a non-joinable session is hidden as a
/// fail-closed denial with no event appended and no delivery. The matching leave event
/// (<c>ParticipantLeft</c>) is the symmetric <see cref="SessionParticipantLeaveService"/>.
///
/// The persisted participant connection metadata is Participants-owned later work, and
/// the join HTTP endpoint is a later story; mapping a denial to a 404/403 response and
/// assigning the participant to a realtime group are deliberately not done here.
/// </summary>
public sealed class SessionParticipantJoinService
{
    private readonly ISessionRepository _sessions;
    private readonly IParticipantRepository _participants;
    private readonly ISessionEventPublisher _eventPublisher;
    private readonly QuotaEnforcementService _quotaEnforcement;
    private readonly TimeProvider _timeProvider;

    public SessionParticipantJoinService(
        ISessionRepository sessions,
        IParticipantRepository participants,
        ISessionEventPublisher eventPublisher,
        QuotaEnforcementService quotaEnforcement,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(eventPublisher);
        ArgumentNullException.ThrowIfNull(quotaEnforcement);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _sessions = sessions;
        _participants = participants;
        _eventPublisher = eventPublisher;
        _quotaEnforcement = quotaEnforcement;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Decides whether the participant identified by
    /// <paramref name="participantId"/> may join the session identified by
    /// <paramref name="sessionId"/>, both scoped to the given organization and
    /// workspace, and returns an admission carrying the validated ids or a typed
    /// fail-closed denial. The decision never crosses the tenant or workspace
    /// boundary: a session or participant outside the requested (organization,
    /// workspace) is hidden as not-found (threat T1/T5). On (and only on) an
    /// admission it appends and delivers the durable <c>ParticipantJoined</c> session
    /// event (CORE-EVT-002) to the session audience before returning; every denial
    /// emits nothing.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Any of the organization, workspace, session or participant id is empty. An
    /// empty id can never address a real resource, so it is rejected rather than
    /// silently treated as not-found (consistent with the repository contracts,
    /// which reject empty ids).
    /// </exception>
    public async Task<SessionJoinResult> JoinAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a real resource. Reject them up front so the
        // decision is a clean ArgumentException, not an ambiguous not-found, and so
        // the fail-fast matches the repository contracts (which also reject empty
        // ids).
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        if (participantId == Guid.Empty)
        {
            throw new ArgumentException("Participant id must not be empty.", nameof(participantId));
        }

        // The session must exist within exactly this organization and workspace.
        // The repository lookup is scoped by both boundaries (organization before
        // workspace), so a session under another tenant or workspace is never
        // returned; its absence is reported as not-found, hiding existence
        // (threat T1/T5).
        var session = await _sessions
            .FindByIdAsync(organizationId, workspaceId, sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return SessionJoinResult.Denied(SessionJoinDenialReason.SessionNotFound);
        }

        // The participant must exist within the same organization and workspace.
        // Same scoping, same existence-hiding as the session lookup (threat T1/T5).
        var participant = await _participants
            .FindByIdAsync(organizationId, workspaceId, participantId, cancellationToken)
            .ConfigureAwait(false);
        if (participant is null)
        {
            return SessionJoinResult.Denied(SessionJoinDenialReason.ParticipantNotFound);
        }

        // Defence in depth (mirrors TenantContext.Create): re-assert that the loaded
        // session and participant each belong to exactly the requested
        // (organization, workspace, id). The repository already scoped the lookups,
        // so a mismatch indicates a programming error, but re-checking here means an
        // admission can never be minted from a session or participant that does not
        // identify the requested boundary. A mismatch hides as not-found rather than
        // leaking that a row was returned (threat T1/T5).
        if (!session.Identifies(organizationId, workspaceId, sessionId))
        {
            return SessionJoinResult.Denied(SessionJoinDenialReason.SessionNotFound);
        }

        if (!participant.Identifies(organizationId, workspaceId, participantId))
        {
            return SessionJoinResult.Denied(SessionJoinDenialReason.ParticipantNotFound);
        }

        // Lifecycle gate (participant): only an active participant holds standing to
        // join; a removed participant is denied (CORE-SES-001). The status is
        // compared by equality, never by ordering: ParticipantStatus is non-linear
        // and persisted by name.
        if (participant.Status != ParticipantStatus.Active)
        {
            return SessionJoinResult.Denied(SessionJoinDenialReason.ParticipantNotActive);
        }

        // Lifecycle gate (session): only a joinable session admits a participant. A
        // session is joinable while Prepared or Live; an ended session is denied
        // (CORE-SES-002). The joinability is expressed by an intention-revealing
        // predicate (IsJoinable) over the status, never by ordering: SessionStatus is
        // non-linear and persisted by name.
        if (!IsJoinable(session.Status))
        {
            return SessionJoinResult.Denied(SessionJoinDenialReason.SessionNotJoinable);
        }

        // Participant-cap gate (CORE-MON-005): the session's server-enforced session.participant.max quota. This is
        // the LAST gate and the only one that CONSUMES, so it runs only after every cheaper existence / isolation /
        // lifecycle check has passed — a join denied for any earlier reason has reserved no slot. The check and the
        // consume are a SINGLE atomic limit-guarded statement (CORE-CONC-004), so concurrent joins racing for the
        // last slot can never both succeed: exactly one is admitted and the rest are quota-exceeded, and the cap is
        // never overrun. It is computed entirely server-side and is fail-closed — the documented free-tier
        // participant cap cannot be bypassed by a client (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md: "Free
        // limits cannot be bypassed by clients"). When no session.participant.max quota governs the deployment the
        // join is ungoverned and proceeds (Core enforces only quotas that exist). A denial here emits NOTHING — an
        // over-cap join is never recorded or delivered, exactly like every other denial.
        var quotaDecision = await _quotaEnforcement
            .TryConsumeAsync(
                EntitlementSubjectType.Session,
                sessionId,
                QuotaEntitlementKeys.SessionParticipantMax,
                amount: 1,
                cancellationToken)
            .ConfigureAwait(false);
        if (!quotaDecision.IsAllowed)
        {
            return SessionJoinResult.Denied(SessionJoinDenialReason.QuotaExceeded);
        }

        // Every check passed: the participant joins. Emit the durable ParticipantJoined session event
        // (CORE-EVT-002) BEFORE returning the admission, so a join that is admitted is a join that is
        // recorded and delivered. The event is published through the reused publisher + recipient resolver
        // (the Realtime module owns delivery); it is a subjectless audience event carrying the participant
        // IDENTIFIER only — never a display name or any PII (threat T7) — so it reaches the hosts (always —
        // host-visible), the observers and every active participant. The actor is the joining participant's
        // linked user, or null (system) for an anonymous participant (docs/09_EVENT_CATALOG.md:
        // "ParticipantJoined | Participant/System").
        var sessionEvent = ParticipantPresenceEvent.Create(
            SessionEventTypes.ParticipantJoined,
            organizationId,
            workspaceId,
            sessionId,
            participantId,
            createdBy: participant.UserProfileId,
            _timeProvider.GetUtcNow());
        await _eventPublisher.PublishAsync(sessionEvent, cancellationToken).ConfigureAwait(false);

        // The result carries the validated, boundary-checked ids only (the realtime group assignment and the
        // persisted connection metadata remain later stories).
        return SessionJoinResult.Admit(organizationId, workspaceId, sessionId, participantId);
    }

    /// <summary>
    /// Whether a session in the given status may be joined by a participant: a
    /// session is joinable only while it is <see cref="SessionStatus.Prepared"/> or
    /// <see cref="SessionStatus.Live"/>. An <see cref="SessionStatus.Ended"/> session
    /// has a closed live timeline and admits no one. The check is an explicit
    /// allow-list of the two joinable states, never a "greater/less than" comparison:
    /// <see cref="SessionStatus"/> is non-linear and persisted by its name, so its
    /// integer values carry no ordering meaning (SessionStatus xmldoc).
    /// </summary>
    private static bool IsJoinable(SessionStatus status)
        => status is SessionStatus.Prepared or SessionStatus.Live;
}
