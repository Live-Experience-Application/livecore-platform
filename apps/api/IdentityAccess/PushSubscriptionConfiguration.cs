// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Relational mapping of <see cref="PushSubscription"/> to the <c>push_subscriptions</c> table
/// (CORE-PUSH-001; csv/database_tables.csv). The table is GLOBAL scope: a subscription is per-principal
/// personal data keyed by the <c>users(id)</c> profile, not tenant data, so it carries no
/// <c>organization_id</c> column (exactly like the <c>users</c> table it hangs off).
///
/// The unique index on (<c>user_id</c>, <c>endpoint</c>) is the database-level guarantee that a principal has
/// at most one row per browser endpoint, so a re-registration of the same endpoint updates the existing row's
/// keys instead of creating a duplicate. The leading <c>user_id</c> column also backs the per-principal
/// list/delete lookups the endpoints and the user-data export issue, all scoped by the caller's own id (threats
/// T1/T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// The subject foreign key into <c>users(id)</c> is <c>ON DELETE CASCADE</c>: a subscription has no meaning
/// without its principal, and the cascade is what makes the data-subject erasure (CORE-PRIV-001) remove a
/// subject's subscriptions automatically — the same cascade the org/workspace membership tables use.
/// </summary>
internal sealed class PushSubscriptionConfiguration : IEntityTypeConfiguration<PushSubscription>
{
    public void Configure(EntityTypeBuilder<PushSubscription> builder)
    {
        builder.ToTable("push_subscriptions");

        builder.HasKey(subscription => subscription.Id);

        builder.Property(subscription => subscription.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(subscription => subscription.UserProfileId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(subscription => subscription.Endpoint)
            .HasColumnName("endpoint")
            .HasMaxLength(PushSubscription.MaxEndpointLength)
            .IsRequired();

        builder.Property(subscription => subscription.P256dh)
            .HasColumnName("p256dh")
            .HasMaxLength(PushSubscription.MaxKeyLength)
            .IsRequired();

        builder.Property(subscription => subscription.Auth)
            .HasColumnName("auth")
            .HasMaxLength(PushSubscription.MaxKeyLength)
            .IsRequired();

        builder.Property(subscription => subscription.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(subscription => subscription.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // A principal has at most one subscription per browser endpoint (the aggregate's natural key).
        // Enforced at the database level so a concurrent re-registration can never create two rows.
        builder.HasIndex(subscription => new { subscription.UserProfileId, subscription.Endpoint })
            .IsUnique()
            .HasDatabaseName("ix_push_subscriptions_user_id_endpoint");

        // Subject foreign key: every subscription references exactly one user profile. Cascade delete removes
        // a subject's subscriptions with their profile (the data-subject erasure, CORE-PRIV-001).
        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(subscription => subscription.UserProfileId)
            .HasConstraintName("fk_push_subscriptions_users_user_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
