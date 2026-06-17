// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Text.RegularExpressions;
using LiveCore.Api.Audit;

namespace LiveCore.Api.UnitTests.Audit;

/// <summary>
/// Pure unit tests for <see cref="AuditLogChain"/> — the per-tenant tamper-evident hash chain over the
/// append-only audit log (CORE-SEC-003, the "Security Hardening" epic). These exercise the hashing and the
/// verification logic in memory (no database): the digest is deterministic, well-formed and binds every field;
/// <see cref="AuditLogChain.Verify"/> passes on an intact chain and reports the first break for a removed,
/// reordered or genesis-tampered entry; and pre-chain (hash-less) legacy entries are counted, not failed. The
/// "an altered PERSISTED entry breaks verification" case is exercised end-to-end against the database in
/// <see cref="AuditLogChainVerifierTests"/>, because an in-memory sealed entry is immutable and cannot be edited.
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class AuditLogChainTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid _organizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>Builds an entry and seals it into the chain at the given sequence/previous hash.</summary>
    private static AuditLogEntry SealedEntry(long sequence, string? previousHash, DateTimeOffset createdAt)
    {
        var entry = AuditLogEntry.Create(
            _organizationId,
            workspaceId: Guid.NewGuid(),
            AuditAction.SessionStarted,
            actorUserProfileId: Guid.NewGuid(),
            resourceType: null,
            resourceId: null,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            createdAt);
        entry.Seal(sequence, previousHash);
        return entry;
    }

    /// <summary>Builds a valid chain of <paramref name="count"/> sealed entries in append order.</summary>
    private static List<AuditLogEntry> SealedChain(int count)
    {
        var chain = new List<AuditLogEntry>(count);
        string? previousHash = null;
        for (var i = 0; i < count; i++)
        {
            var entry = SealedEntry(i + 1, previousHash, _now.AddSeconds(i));
            chain.Add(entry);
            previousHash = entry.EntryHash;
        }

        return chain;
    }

    [Fact]
    public void Sealing_produces_a_well_formed_lowercase_hex_digest()
    {
        var entry = SealedEntry(1, previousHash: null, _now);

        Assert.NotNull(entry.EntryHash);
        Assert.Equal(AuditLogChain.HashHexLength, entry.EntryHash!.Length);
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), entry.EntryHash);
        // The genesis entry has no predecessor.
        Assert.Null(entry.PreviousHash);
        Assert.Equal(1, entry.Sequence);
    }

    [Fact]
    public void ComputeEntryHash_is_deterministic_and_binds_the_previous_link()
    {
        var entry = SealedEntry(2, previousHash: new string('a', 64), _now);

        // Deterministic: the same entry and previous hash always hash to the same value (its sealed hash).
        Assert.Equal(entry.EntryHash, AuditLogChain.ComputeEntryHash(entry, entry.PreviousHash));

        // The previous-hash link is part of the digest: a different predecessor yields a different hash, so the
        // chain cannot be re-pointed without detection.
        var rehashedWithDifferentPredecessor = AuditLogChain.ComputeEntryHash(entry, new string('b', 64));
        Assert.NotEqual(entry.EntryHash, rehashedWithDifferentPredecessor);
    }

    [Fact]
    public void Sealing_twice_is_rejected()
    {
        var entry = SealedEntry(1, previousHash: null, _now);

        Assert.Throws<InvalidOperationException>(() => entry.Seal(2, entry.EntryHash));
    }

    [Fact]
    public void Verify_passes_on_an_untampered_chain()
    {
        var chain = SealedChain(5);

        var result = AuditLogChain.Verify(chain);

        Assert.True(result.IsValid);
        Assert.Equal(5, result.VerifiedCount);
        Assert.Equal(0, result.LegacyCount);
        Assert.Null(result.FirstBrokenEntryId);
    }

    [Fact]
    public void Verify_passes_on_an_empty_chain()
    {
        var result = AuditLogChain.Verify([]);

        Assert.True(result.IsValid);
        Assert.Equal(0, result.VerifiedCount);
    }

    [Fact]
    public void Verify_detects_a_removed_entry()
    {
        var chain = SealedChain(3);

        // Drop the middle entry: the third entry's previous-hash link no longer matches the (now first) entry
        // and its sequence is no longer contiguous — a deletion is detected.
        var withMiddleRemoved = new List<AuditLogEntry> { chain[0], chain[2] };

        var result = AuditLogChain.Verify(withMiddleRemoved);

        Assert.False(result.IsValid);
        Assert.Equal(chain[2].Id, result.FirstBrokenEntryId);
        Assert.Equal(chain[2].Sequence, result.FirstBrokenSequence);
        Assert.Equal(1, result.VerifiedCount);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void Verify_detects_a_non_genesis_first_entry()
    {
        var chain = SealedChain(3);

        // Start the chain at the second entry: the first verified entry must be the genesis (null previous hash),
        // but this one links to a predecessor — an entry was removed from the front.
        var startingMidChain = new List<AuditLogEntry> { chain[1], chain[2] };

        var result = AuditLogChain.Verify(startingMidChain);

        Assert.False(result.IsValid);
        Assert.Equal(chain[1].Id, result.FirstBrokenEntryId);
    }

    [Fact]
    public void Verify_counts_pre_chain_legacy_entries_without_failing()
    {
        // A legacy entry written before the chain existed carries no hash. It is counted, not verified, and the
        // first hashed entry after it starts a fresh genesis.
        var legacy = AuditLogEntry.Create(
            _organizationId,
            workspaceId: null,
            AuditAction.MemberInvited,
            actorUserProfileId: null,
            resourceType: null,
            resourceId: null,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            _now);
        Assert.Null(legacy.EntryHash);

        var genesis = SealedEntry(2, previousHash: null, _now.AddSeconds(1));

        var result = AuditLogChain.Verify([legacy, genesis]);

        Assert.True(result.IsValid);
        Assert.Equal(1, result.LegacyCount);
        Assert.Equal(1, result.VerifiedCount);
    }
}
