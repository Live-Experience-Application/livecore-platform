// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Realtime;

/// <summary>
/// The cross-instance seam for the per-instance <see cref="RealtimeConnectionRegistry"/> (CORE-RES-008, the
/// "Multi-Instance Runtime Correctness" epic). The registry's abort handle is an in-process
/// <c>HubCallerContext.Abort</c>, so on its own an <see cref="IRealtimeConnectionEvictor"/> eviction aborts only the
/// sockets held by the instance that handled the demotion/removal; a still-open socket the SAME user holds on
/// another API replica lingers until it reconnects. This seam closes that window: after evicting its own held
/// connections, the registry PUBLISHES an opaque eviction descriptor over the deployment's configured Valkey/Redis
/// backplane (the same <c>Realtime:Backplane:*</c> connection the realtime fan-out uses; docs/02_ARCHITECTURE.md /
/// docs/11_REALTIME_SYNC.md), and every replica's <see cref="RealtimeConnectionEvictionListener"/> applies it to its
/// OWN registry through <see cref="RealtimeConnectionRegistry.ApplyRemoteEviction"/>, so the demoted/removed user's
/// socket is aborted on every replica within a bounded window.
///
/// <para>
/// CORRECTNESS / FAIL-CLOSED (the story's "single-instance behaviour is unchanged; no new fail-open path"). The
/// cross-instance broadcast can only ever cause MORE eviction, never less, so it cannot widen an audience:
/// </para>
///
/// <list type="number">
///   <item><b>Local eviction is unconditional and first.</b> The originating instance aborts its own held
///   connections BEFORE it publishes, so a failed/dropped broadcast leaves the local eviction intact and a peer
///   simply keeps its socket until that client reconnects (the existing single-instance posture) — never a widened
///   audience. <see cref="Publish"/> is best-effort and MUST NOT throw (a broadcast failure must never turn a
///   successful demotion/removal into a request error).</item>
///   <item><b>Eviction only ever removes a connection.</b> A received descriptor only ever ABORTS matching
///   connections; the authoritative re-admission stays the existing resolver, so a reconnecting client is
///   authorized from scratch (a demoted host re-joins only its new role's groups; a removed participant is denied).</item>
///   <item><b>Apply-only on receipt.</b> A received eviction is applied LOCALLY ONLY
///   (<see cref="RealtimeConnectionRegistry.ApplyRemoteEviction"/>) and never re-published, so it cannot echo
///   forever across the backplane.</item>
/// </list>
///
/// <para>
/// The descriptor carries only opaque surrogate ids (the tenant/workspace/session and the participant or subject id;
/// <see cref="RealtimeConnectionEviction"/>) — never a display name, token or any content (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md). With no backplane configured the host uses the
/// <see cref="NullRealtimeConnectionEvictionBackplane"/> no-op, so single-instance behaviour is exactly as before
/// (the registry evicts its own held sockets and nothing is broadcast). It is the realtime-connection counterpart of
/// the authorization-cache invalidation backplane (CORE-RES-007).
/// </para>
/// </summary>
internal interface IRealtimeConnectionEvictionBackplane
{
    /// <summary>
    /// Best-effort broadcasts a local connection eviction described by <paramref name="evictionToken"/> to the other
    /// API replicas over the configured backplane. MUST NOT throw: a broadcast failure leaves the already-performed
    /// local eviction intact (a peer keeps its socket only until reconnect), so it never breaks the demotion/removal
    /// that triggered it.
    /// </summary>
    void Publish(string evictionToken);

    /// <summary>
    /// Subscribes <paramref name="onRemoteEviction"/> to eviction descriptors other replicas publish, so this
    /// instance can abort the matching connections it holds. Called once at startup by the
    /// <see cref="RealtimeConnectionEvictionListener"/>.
    /// </summary>
    void Subscribe(Action<string> onRemoteEviction);
}

/// <summary>
/// The no-op <see cref="IRealtimeConnectionEvictionBackplane"/> used when no backplane is configured (a
/// single-instance deployment, the documented single-instance constraint). It neither publishes nor receives, so the
/// realtime eviction behaves exactly as it did before CORE-RES-008: an eviction aborts the sockets this instance
/// holds and there are no peer replicas to notify.
/// </summary>
internal sealed class NullRealtimeConnectionEvictionBackplane : IRealtimeConnectionEvictionBackplane
{
    /// <summary>The shared, stateless no-op instance.</summary>
    public static NullRealtimeConnectionEvictionBackplane Instance { get; } = new();

    /// <inheritdoc />
    public void Publish(string evictionToken)
    {
        // No backplane configured: nothing to broadcast to. The local eviction already happened in the registry.
    }

    /// <inheritdoc />
    public void Subscribe(Action<string> onRemoteEviction)
    {
        // No backplane configured: no peer publishes, so there is nothing to receive.
    }
}
