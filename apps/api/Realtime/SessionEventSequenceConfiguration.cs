// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Relational mapping of <see cref="SessionEventSequence"/> to the <c>session_event_sequences</c> table
/// (CORE-RTC-001): the per-session counter that allocates the gap-free, strictly monotonic sequence numbers
/// of the append-only <c>session_events</c> stream. One row per session, keyed by <c>session_id</c>.
///
/// The <c>session_id</c> primary key is also a foreign key into <c>sessions(id)</c> that CASCADES on delete:
/// the counter has no meaning without its session, so it is removed with the session exactly as the session
/// event stream is (the counter and the stream it feeds share the session's lifetime). The single
/// <c>last_sequence</c> column holds the highest number handed out; it is advanced only by the append path's
/// atomic <c>INSERT ... ON CONFLICT DO UPDATE</c> (<see cref="SessionEventRepository.AppendAsync"/>), never
/// by a tracked write.
/// </summary>
internal sealed class SessionEventSequenceConfiguration : IEntityTypeConfiguration<SessionEventSequence>
{
    public void Configure(EntityTypeBuilder<SessionEventSequence> builder)
    {
        builder.ToTable("session_event_sequences");

        builder.HasKey(counter => counter.SessionId);

        builder.Property(counter => counter.SessionId)
            .HasColumnName("session_id")
            .ValueGeneratedNever();

        builder.Property(counter => counter.LastSequence)
            .HasColumnName("last_sequence")
            .IsRequired();

        // The session owns its counter: cascade delete removes the counter with the session (mirrors the
        // session_events cascade). The counter is keyed by the session, so the key IS the foreign key.
        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(counter => counter.SessionId)
            .HasConstraintName("fk_session_event_sequences_sessions_session_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
