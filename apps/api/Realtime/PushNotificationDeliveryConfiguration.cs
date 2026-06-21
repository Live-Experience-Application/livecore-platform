// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Relational mapping of <see cref="PushNotificationDelivery"/> to the <c>push_notification_deliveries</c> table
/// (CORE-PUSH-002; csv/database_tables.csv). The table is the closed-app push OUTBOX: tenant-scoped rows the API
/// writes after a recipient-filtered session event commits and the worker drains.
///
/// Both foreign keys are <c>ON DELETE CASCADE</c>: the <c>organization_id</c> into <c>organizations(id)</c> makes a
/// tenant teardown (CORE-PRIV-002) drop the tenant's pending pushes with it, and the <c>user_id</c> into
/// <c>users(id)</c> makes a data-subject erasure (CORE-PRIV-001) drop the subject's pending pushes — the same
/// cascade the <c>push_subscriptions</c> table uses. The <c>session_id</c> and <c>session_event_id</c> are recorded
/// IDENTIFIERS (not foreign keys), mirroring the session event's polymorphic visibility subject: the row is a
/// transient hand-off, never a content store (threats T2/T7).
/// </summary>
internal sealed class PushNotificationDeliveryConfiguration : IEntityTypeConfiguration<PushNotificationDelivery>
{
    public void Configure(EntityTypeBuilder<PushNotificationDelivery> builder)
    {
        builder.ToTable("push_notification_deliveries");

        builder.HasKey(delivery => delivery.Id);

        builder.Property(delivery => delivery.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(delivery => delivery.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(delivery => delivery.SessionId)
            .HasColumnName("session_id")
            .IsRequired();

        builder.Property(delivery => delivery.SessionEventId)
            .HasColumnName("session_event_id")
            .IsRequired();

        builder.Property(delivery => delivery.RecipientUserProfileId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(delivery => delivery.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // Tenant foreign key: every pending push belongs to exactly one organization. Cascade delete removes a
        // tenant's pending pushes with the tenant root (CORE-PRIV-002).
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(delivery => delivery.OrganizationId)
            .HasConstraintName("fk_push_notification_deliveries_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Recipient foreign key: every pending push targets exactly one user profile. Cascade delete removes a
        // subject's pending pushes when the subject is erased (CORE-PRIV-001), the same cascade push_subscriptions
        // uses.
        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(delivery => delivery.RecipientUserProfileId)
            .HasConstraintName("fk_push_notification_deliveries_users_user_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
