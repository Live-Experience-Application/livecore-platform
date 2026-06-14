using LiveCore.Api.Observability;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LiveCore.Api.Realtime;

/// <summary>
/// The session realtime hub (CORE-RT-001 authentication; CORE-RT-002 server-managed groups). The
/// Realtime module owns "connection tracking, hub groups, event delivery, reconnect replay" and "may not
/// send unfiltered events" (docs/05_MODULE_CONTRACTS.md). This is the authenticated front door every
/// realtime capability connects through, and now the place a connection is placed into its
/// SERVER-MANAGED groups.
///
/// AUTHENTICATION (CORE-RT-001). The hub is <c>[Authorize]</c>, so SignalR requires a validated OIDC
/// bearer token to negotiate and connect (an unauthenticated client is rejected before any hub code
/// runs). Because browser WebSocket clients cannot send the <c>Authorization</c> header, the token is
/// also accepted from the <c>access_token</c> query string for hub paths only (see <c>HubBearerToken</c>
/// / <c>OidcAuthenticationExtensions</c>); it is validated by the same bearer pipeline either way.
///
/// SERVER-MANAGED GROUPS (CORE-RT-002). On connect the hub resolves the caller's authorized relationship
/// to a session and joins ONLY the server-computed groups for that relationship
/// (<see cref="RealtimeConnectionResolver"/> / <see cref="RealtimeGroups"/>). The connection supplies
/// IDENTIFIERS on the connection URL — <c>?organizationSlug=…&amp;sessionId=…</c> and, for a participant,
/// <c>&amp;participantId=…</c> — never group names, so a client can never choose a group or subscribe to
/// another participant's feed (docs/11_REALTIME_SYNC.md; threat T3 in docs/07_SECURITY_THREAT_MODEL.md).
/// The whole connect is FAIL-CLOSED: a missing resolver (persistence off), a malformed identifier, an
/// unauthenticated/unmappable principal, a denied tenant, an unknown session, or a caller with no
/// legitimate relationship all ABORT the connection — and the abort is indistinguishable, so a probe
/// learns nothing about what exists (threats T1/T5).
///
/// The hub still exposes NO client-callable methods and sends NO events: event append/delivery is
/// CORE-RT-003, per-recipient projection CORE-RT-004, reconnect replay CORE-RT-005, scale-out
/// CORE-RT-006. This story only establishes group MEMBERSHIP, so it cannot leak an event (threat T3).
/// </summary>
[Authorize]
internal sealed class SessionHub : Hub
{
    // Marks a connection that was COUNTED on the live realtime-connection gauge, kept in the per-connection
    // Context.Items so the disconnect handler decrements only a connection it incremented (an aborted
    // connection is never counted, so the gauge never drifts).
    private const string _meteredConnectionKey = "livecore.realtime.metered";

    private readonly LiveCoreMetrics _metrics;
    private readonly RealtimeConnectionRegistry _connections;

    public SessionHub(LiveCoreMetrics metrics, RealtimeConnectionRegistry connections)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(connections);
        _metrics = metrics;
        _connections = connections;
    }

    /// <summary>
    /// Resolves the connection's server-managed groups and joins them, or aborts the connection
    /// fail-closed. The authenticated principal and the connection's identifiers
    /// (<c>organizationSlug</c>, <c>sessionId</c>, optional <c>participantId</c>) are read from the
    /// initial connection request and handed to <see cref="RealtimeConnectionResolver"/>; the groups it
    /// returns are server-computed, never client-chosen.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext is null)
        {
            Context.Abort();
            return;
        }

        // The resolver is registered only when persistence is configured; without it the hub cannot
        // authorize a connection, so it fails closed exactly as the REST endpoints return 503.
        var resolver = httpContext.RequestServices.GetService<RealtimeConnectionResolver>();
        if (resolver is null)
        {
            Context.Abort();
            return;
        }

        if (!TryReadConnectionParameters(httpContext, out var organizationSlug, out var sessionId, out var participantId))
        {
            Context.Abort();
            return;
        }

        var admission = await resolver
            .ResolveAsync(Context.User, organizationSlug, sessionId, participantId, Context.ConnectionAborted)
            .ConfigureAwait(false);
        if (!admission.Admitted)
        {
            Context.Abort();
            return;
        }

        foreach (var group in admission.Groups)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted).ConfigureAwait(false);
        }

        // Record the admitted connection so a later re-authorization (CORE-RTC-002) can find and evict it: a
        // participant removal or a member role change aborts exactly the affected connections through the
        // RealtimeConnectionRegistry, so a removed participant's still-open socket and a demoted host's
        // host/observer deliveries stop WITHOUT waiting for a client-initiated reconnect (threat T3). The
        // abort handle is this connection's own HubCallerContext.Abort; the subject is the resolver's
        // server-computed authorized facts (never client input). Eviction only ever removes a connection, so
        // it can never widen an audience.
        var hubContext = Context;
        _connections.Register(hubContext.ConnectionId, admission.Subject!.Value, hubContext.Abort);

        // Count the admitted connection on the live realtime-connection gauge (CORE-OBS-001, the docs/15
        // "realtime connections" signal). Only the success path reaches here — an aborted connection returned
        // above and is never counted — and the marker lets OnDisconnectedAsync decrement exactly this
        // connection, so the gauge stays balanced.
        Context.Items[_meteredConnectionKey] = true;
        _metrics.RecordRealtimeConnectionOpened();

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Decrements the live realtime-connection gauge when an admitted connection disconnects. It decrements
    /// only a connection that was counted on connect (the <see cref="_meteredConnectionKey"/> marker), so a
    /// connection aborted during <see cref="OnConnectedAsync"/> — which was never counted — never drives the
    /// gauge negative.
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Forget the connection so the registry never accumulates dead entries. This runs whether the
        // disconnect was client-initiated or the result of an eviction abort (CORE-RTC-002); removing an
        // unknown id is a safe no-op, so a connection aborted by an eviction is cleared exactly once.
        _connections.Unregister(Context.ConnectionId);

        if (Context.Items.ContainsKey(_meteredConnectionKey))
        {
            Context.Items.Remove(_meteredConnectionKey);
            _metrics.RecordRealtimeConnectionClosed();
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the connection identifiers from the initial request's query string. Returns
    /// <see langword="false"/> when a required identifier is missing or malformed (the session id must
    /// be a GUID; a present participant id must be a GUID), so the hub aborts. The values are not
    /// authorized here — that is the resolver's job; this only parses the shape.
    /// </summary>
    private static bool TryReadConnectionParameters(
        HttpContext httpContext,
        out string organizationSlug,
        out Guid sessionId,
        out Guid? participantId)
    {
        organizationSlug = httpContext.Request.Query["organizationSlug"].ToString();
        sessionId = Guid.Empty;
        participantId = null;

        if (!Guid.TryParse(httpContext.Request.Query["sessionId"].ToString(), out sessionId))
        {
            return false;
        }

        var participantRaw = httpContext.Request.Query["participantId"].ToString();
        if (!string.IsNullOrWhiteSpace(participantRaw))
        {
            if (!Guid.TryParse(participantRaw, out var parsedParticipantId))
            {
                return false;
            }

            participantId = parsedParticipantId;
        }

        return true;
    }
}
