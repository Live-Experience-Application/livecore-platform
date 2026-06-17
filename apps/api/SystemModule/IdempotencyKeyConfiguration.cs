// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.SystemModule;

/// <summary>
/// Relational mapping of <see cref="IdempotencyKey"/> to the <c>idempotency_keys</c> table
/// (CORE-VIS-004; csv/database_tables.csv: module System, scope <c>scope</c>, "Retry safety"). The
/// table is generic retry-safety infrastructure and is NOT tenant-scoped at the row level — there is
/// no <c>organization_id</c> column and no tenant foreign key, because the tenant (and any other
/// context) is folded into the opaque <c>scope</c> partition string the calling feature composes. So
/// idempotency keys never reference the tenant tables and the System module stays decoupled.
///
/// The single documented critical index is the UNIQUE composite index on (<c>scope</c>, <c>key</c>)
/// — <c>idempotency_keys(scope, key)</c> (docs/10_DATABASE_SCHEMA.md). Its uniqueness IS the
/// idempotency guarantee: the same client key within a scope can be recorded only once, so a
/// concurrent or repeated insert is rejected at the database and surfaces as
/// <see cref="IdempotencyKeyAddResult.Duplicate"/>. Both columns are bounded strings.
/// </summary>
internal sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.ToTable("idempotency_keys");

        builder.HasKey(idempotencyKey => idempotencyKey.Id);

        builder.Property(idempotencyKey => idempotencyKey.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(idempotencyKey => idempotencyKey.Scope)
            .HasColumnName("scope")
            .HasMaxLength(IdempotencyKey.MaxScopeLength)
            .IsRequired();

        builder.Property(idempotencyKey => idempotencyKey.Key)
            .HasColumnName("key")
            .HasMaxLength(IdempotencyKey.MaxKeyLength)
            .IsRequired();

        // Optional reference to the resource the guarded request produced (CORE-DX-004): nullable, because a
        // state-idempotent command (reveal/hide) records the key with no result. A create or
        // purchase-verification key carries the created resource's id so a retry returns the original result.
        builder.Property(idempotencyKey => idempotencyKey.ResultId)
            .HasColumnName("result_id");

        builder.Property(idempotencyKey => idempotencyKey.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // Documented critical index idempotency_keys(scope, key), UNIQUE: the uniqueness is the
        // idempotency guarantee (docs/10_DATABASE_SCHEMA.md).
        builder.HasIndex(idempotencyKey => new { idempotencyKey.Scope, idempotencyKey.Key })
            .HasDatabaseName("ix_idempotency_keys_scope_key")
            .IsUnique();
    }
}
