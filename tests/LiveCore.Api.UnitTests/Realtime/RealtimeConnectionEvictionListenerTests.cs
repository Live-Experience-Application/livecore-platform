// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Realtime;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Unit tests for <see cref="RealtimeConnectionEvictionListener"/> (CORE-RES-008): the hosted service that wires the
/// cross-instance eviction RECEIVE side. On start it must subscribe to the backplane and route every descriptor a
/// peer replica publishes into THIS instance's <see cref="RealtimeConnectionRegistry.ApplyRemoteEviction"/>, so a
/// demotion/removal broadcast by another replica aborts the matching still-open socket here too. A fake backplane
/// captures the subscribed handler so the test can drive a "received message" deterministically without a real
/// server. Generic vocabulary only (AGENTS.md).
/// </summary>
public sealed class RealtimeConnectionEvictionListenerTests
{
    private static readonly Guid _org = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _workspace = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _session = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task On_start_it_subscribes_and_routes_a_received_descriptor_into_the_local_registry()
    {
        var backplane = new CapturingBackplane();
        var registry = new RealtimeConnectionRegistry(backplane);
        var listener = new RealtimeConnectionEvictionListener(backplane, registry);

        // A live host/observer member connection this instance holds in the known tenant/workspace.
        var userProfileId = Guid.NewGuid();
        var aborted = false;
        registry.Register(
            "conn",
            new RealtimeConnectionSubject(_org, _workspace, _session, userProfileId, ParticipantId: null),
            () => aborted = true);

        // Before start there is no subscriber.
        Assert.Null(backplane.Handler);

        await listener.StartAsync(CancellationToken.None);
        Assert.NotNull(backplane.Handler);

        // A peer replica publishes the member's eviction; the listener routes it into this instance's registry and
        // the matching socket is aborted.
        backplane.Handler!(
            RealtimeConnectionEviction.ForWorkspaceMember(_org, _workspace, userProfileId).Serialize());

        Assert.True(aborted);

        await listener.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Construction_rejects_null_arguments()
    {
        var backplane = new CapturingBackplane();
        var registry = new RealtimeConnectionRegistry(backplane);

        Assert.Throws<ArgumentNullException>(() => new RealtimeConnectionEvictionListener(null!, registry));
        Assert.Throws<ArgumentNullException>(() => new RealtimeConnectionEvictionListener(backplane, null!));
    }

    /// <summary>A backplane that captures the single handler the listener subscribes, so the test can invoke it.</summary>
    private sealed class CapturingBackplane : IRealtimeConnectionEvictionBackplane
    {
        public Action<string>? Handler { get; private set; }

        public void Publish(string evictionToken)
        {
            // Not exercised by these tests.
        }

        public void Subscribe(Action<string> onRemoteEviction) => Handler = onRemoteEviction;
    }
}
