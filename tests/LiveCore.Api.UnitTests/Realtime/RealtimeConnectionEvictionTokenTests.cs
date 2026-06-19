// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Realtime;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Unit tests for <see cref="RealtimeConnectionEviction"/> (CORE-RES-008) — the opaque descriptor a connection
/// eviction is broadcast as across API replicas. They prove the wire format round-trips losslessly for BOTH kinds
/// and that parsing is DEFENSIVE: a malformed/unrecognized token on the shared backplane channel is rejected, so it
/// can never abort an unintended connection (the cross-instance counterpart of the authorization-cache invalidation
/// token guard). The token carries only "N"-format surrogate Guids, never content (threat T7); generic vocabulary
/// only (AGENTS.md).
/// </summary>
public sealed class RealtimeConnectionEvictionTokenTests
{
    [Fact]
    public void A_participant_descriptor_round_trips_through_serialize_and_parse()
    {
        var original = RealtimeConnectionEviction.ForParticipant(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        Assert.True(RealtimeConnectionEviction.TryParse(original.Serialize(), out var parsed));
        Assert.Equal(original, parsed);
        Assert.Equal(RealtimeConnectionEvictionKind.Participant, parsed.Kind);
    }

    [Fact]
    public void A_member_descriptor_round_trips_through_serialize_and_parse()
    {
        var original = RealtimeConnectionEviction.ForWorkspaceMember(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        Assert.True(RealtimeConnectionEviction.TryParse(original.Serialize(), out var parsed));
        Assert.Equal(original, parsed);
        Assert.Equal(RealtimeConnectionEvictionKind.WorkspaceMember, parsed.Kind);
        // A member eviction does not use the session id.
        Assert.Equal(Guid.Empty, parsed.SessionId);
    }

    [Fact]
    public void The_two_kinds_serialize_to_distinct_prefixes()
    {
        var org = Guid.CreateVersion7();
        var workspace = Guid.CreateVersion7();

        var participant = RealtimeConnectionEviction
            .ForParticipant(org, workspace, Guid.CreateVersion7(), Guid.CreateVersion7())
            .Serialize();
        var member = RealtimeConnectionEviction.ForWorkspaceMember(org, workspace, Guid.CreateVersion7()).Serialize();

        Assert.StartsWith("p:", participant);
        Assert.StartsWith("m:", member);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("p")]
    [InlineData("not-a-token")]
    [InlineData("x:00000000000000000000000000000000")]                                  // unknown kind prefix
    [InlineData("p:00000000000000000000000000000000")]                                  // participant: too few segments
    [InlineData("p:0:0:0:0")]                                                            // participant: not Guids
    [InlineData("m:00000000000000000000000000000000:00000000000000000000000000000000")] // member: too few segments
    [InlineData("m:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz:00000000000000000000000000000000:00000000000000000000000000000000")] // not hex
    public void Parsing_a_malformed_token_fails_closed(string? token)
        => Assert.False(RealtimeConnectionEviction.TryParse(token, out _));
}
