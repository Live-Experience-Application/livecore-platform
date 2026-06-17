// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Realtime;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Unit tests for <see cref="RealtimeGroups"/> (CORE-RT-002) — the canonical, server-owned group-name
/// builders. They pin the exact names against the documented connection model
/// (docs/11_REALTIME_SYNC.md) so the addressing can never silently drift, and verify every builder
/// rejects an empty id (an empty id could form an overlapping/meaningless group — fail fast, threat T3).
/// </summary>
public sealed class RealtimeGroupsTests
{
    private static readonly Guid _org = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _workspace = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _session = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid _participant = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void Group_names_match_the_documented_connection_model()
    {
        Assert.Equal($"org:{_org}", RealtimeGroups.Organization(_org));
        Assert.Equal($"workspace:{_workspace}:hosts", RealtimeGroups.WorkspaceHosts(_workspace));
        Assert.Equal($"session:{_session}:hosts", RealtimeGroups.SessionHosts(_session));
        Assert.Equal(
            $"session:{_session}:participant:{_participant}",
            RealtimeGroups.SessionParticipant(_session, _participant));
        Assert.Equal($"session:{_session}:observers", RealtimeGroups.SessionObservers(_session));
    }

    [Fact]
    public void Two_participants_in_one_session_get_distinct_groups()
    {
        var other = Guid.Parse("55555555-5555-5555-5555-555555555555");

        Assert.NotEqual(
            RealtimeGroups.SessionParticipant(_session, _participant),
            RealtimeGroups.SessionParticipant(_session, other));
    }

    [Fact]
    public void Every_builder_rejects_an_empty_id()
    {
        Assert.Throws<ArgumentException>(() => RealtimeGroups.Organization(Guid.Empty));
        Assert.Throws<ArgumentException>(() => RealtimeGroups.WorkspaceHosts(Guid.Empty));
        Assert.Throws<ArgumentException>(() => RealtimeGroups.SessionHosts(Guid.Empty));
        Assert.Throws<ArgumentException>(() => RealtimeGroups.SessionParticipant(Guid.Empty, _participant));
        Assert.Throws<ArgumentException>(() => RealtimeGroups.SessionParticipant(_session, Guid.Empty));
        Assert.Throws<ArgumentException>(() => RealtimeGroups.SessionObservers(Guid.Empty));
    }
}
