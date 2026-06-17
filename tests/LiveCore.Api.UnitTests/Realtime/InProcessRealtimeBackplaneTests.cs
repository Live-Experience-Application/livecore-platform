// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Tests for <see cref="InProcessRealtimeBackplane"/> (CORE-RT-006), the default single-instance realtime
/// scale-out transport. It is the seam a multi-instance deployment swaps a Valkey/Redis-compatible
/// backplane into (docs/11_REALTIME_SYNC.md "Scale-out"); the default just forwards one already-authorized
/// delivery — a recipient-safe payload to one server-managed group — to this instance's connections over
/// <see cref="IHubContext{SessionHub}"/>. These tests use a recording hub context to pin that it forwards
/// the group, client method and payload VERBATIM (it must not re-route or broaden the audience), and that
/// it fails fast on a missing group/method/payload. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class InProcessRealtimeBackplaneTests
{
    [Fact]
    public async Task It_forwards_the_group_method_and_payload_to_the_hub_group_verbatim()
    {
        var hub = new RecordingHubContext();
        var backplane = new InProcessRealtimeBackplane(hub);
        var payload = new { resourceId = "x" };

        await backplane.SendToGroupAsync("session:abc:participant:1", "SessionEvent", payload, CancellationToken.None);

        var send = Assert.Single(hub.Clients.GroupSends);
        Assert.Equal("session:abc:participant:1", send.Group);
        Assert.Equal("SessionEvent", send.Method);
        Assert.Same(payload, send.Payload);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task It_rejects_a_missing_group(string? group)
    {
        var backplane = new InProcessRealtimeBackplane(new RecordingHubContext());

        // Null throws ArgumentNullException, empty/whitespace throws ArgumentException — both fail closed.
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            backplane.SendToGroupAsync(group!, "SessionEvent", new object(), CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task It_rejects_a_missing_method(string? method)
    {
        var backplane = new InProcessRealtimeBackplane(new RecordingHubContext());

        // Null throws ArgumentNullException, empty/whitespace throws ArgumentException — both fail closed.
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            backplane.SendToGroupAsync("session:abc:hosts", method!, new object(), CancellationToken.None));
    }

    [Fact]
    public async Task It_rejects_a_null_payload()
    {
        var backplane = new InProcessRealtimeBackplane(new RecordingHubContext());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            backplane.SendToGroupAsync("session:abc:hosts", "SessionEvent", null!, CancellationToken.None));
    }

    [Fact]
    public void It_rejects_a_null_hub()
        => Assert.Throws<ArgumentNullException>(() => new InProcessRealtimeBackplane(null!));

    // --- Test doubles ----------------------------------------------------------

    private sealed class RecordingHubContext : IHubContext<SessionHub>
    {
        public RecordingHubClients Clients { get; } = new();

        IHubClients IHubContext<SessionHub>.Clients => Clients;

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class RecordingHubClients : IHubClients
    {
        public List<(string Group, string Method, object? Payload)> GroupSends { get; } = [];

        public IClientProxy Group(string groupName) => new GroupRecorder(groupName, GroupSends);

        public IClientProxy All => throw new NotSupportedException();

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        public IClientProxy Client(string connectionId) => throw new NotSupportedException();

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds)
            => throw new NotSupportedException();

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

        public IClientProxy User(string userId) => throw new NotSupportedException();

        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class GroupRecorder : IClientProxy
    {
        private readonly string _group;
        private readonly List<(string Group, string Method, object? Payload)> _sends;

        public GroupRecorder(string group, List<(string Group, string Method, object? Payload)> sends)
        {
            _group = group;
            _sends = sends;
        }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            _sends.Add((_group, method, args.Length > 0 ? args[0] : null));
            return Task.CompletedTask;
        }
    }
}
