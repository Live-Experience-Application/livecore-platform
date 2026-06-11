using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Relational mapping of <see cref="UserProfile"/> to the <c>users</c> table
/// (CORE-ID-002; csv/database_tables.csv). The table is global scope: it
/// references identities, not tenant data, so it carries no organization
/// column (tenant-scoped tables start with the Organizations module
/// stories).
///
/// The unique index on (issuer, subject_id) is the database-level tenant
/// boundary guarantee for identities: one OIDC identity pair maps to at
/// most one profile row, and the same subject under a different issuer is a
/// different row (threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// Deployment requirement: equality on issuer and subject_id must stay
/// exact and case-sensitive, so the target database needs a deterministic
/// default collation (PostgreSQL default "C"/locale collations qualify). A
/// nondeterministic case-insensitive collation would silently weaken the
/// unique index and the lookups. Pinning the column collation in the model
/// is planned with the first story that runs migrations against real
/// PostgreSQL (CORE-ID-006 isolation tests).
/// </summary>
internal sealed class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.ToTable("users");

        builder.HasKey(profile => profile.Id);

        builder.Property(profile => profile.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(profile => profile.Issuer)
            .HasColumnName("issuer")
            .HasMaxLength(OidcPrincipal.MaxIssuerLength)
            .IsRequired();

        builder.Property(profile => profile.SubjectId)
            .HasColumnName("subject_id")
            .HasMaxLength(OidcPrincipal.MaxSubjectIdLength)
            .IsRequired();

        builder.Property(profile => profile.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(OidcPrincipal.MaxDisplayNameLength);

        builder.Property(profile => profile.Email)
            .HasColumnName("email")
            .HasMaxLength(OidcPrincipal.MaxEmailLength);

        builder.Property(profile => profile.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(profile => profile.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(profile => new { profile.Issuer, profile.SubjectId })
            .IsUnique()
            .HasDatabaseName("ix_users_issuer_subject_id");
    }
}
