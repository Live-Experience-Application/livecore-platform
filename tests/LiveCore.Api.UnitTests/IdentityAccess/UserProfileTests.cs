using LiveCore.Api.IdentityAccess;

namespace LiveCore.Api.UnitTests.IdentityAccess;

/// <summary>
/// Unit tests for the <see cref="UserProfile"/> invariants (CORE-ID-002):
/// the profile is keyed by the exact OIDC identity pair (issuer, subject),
/// metadata mirrors the provider, and no operation ever crosses identities
/// (threat T5 in docs/07_SECURITY_THREAT_MODEL.md). ToString stays log-safe
/// (threat T7).
/// </summary>
public class UserProfileTests
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _foreignIssuer = "https://id.foreign.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";
    private const string _foreignSubject = "11111111-2222-3333-4444-555555555555";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);

    private static OidcPrincipal CreateUserPrincipal(
        string issuer = _issuer,
        string subjectId = _subject,
        string? displayName = null,
        string? email = null)
        => new(PrincipalType.User, issuer, subjectId, displayName, email);

    [Fact]
    public void CreateFromPrincipal_copies_identity_and_metadata()
    {
        var principal = CreateUserPrincipal(displayName: "Casey Example", email: "casey@example.test");

        var profile = UserProfile.CreateFromPrincipal(principal, _createdAt);

        Assert.NotEqual(Guid.Empty, profile.Id);
        Assert.Equal(_issuer, profile.Issuer);
        Assert.Equal(_subject, profile.SubjectId);
        Assert.Equal("Casey Example", profile.DisplayName);
        Assert.Equal("casey@example.test", profile.Email);
        Assert.Equal(_createdAt, profile.CreatedAt);
        Assert.Equal(_createdAt, profile.UpdatedAt);
    }

    [Fact]
    public void CreateFromPrincipal_generates_time_ordered_unique_ids()
    {
        var principal = CreateUserPrincipal();

        var first = UserProfile.CreateFromPrincipal(principal, _createdAt);
        var second = UserProfile.CreateFromPrincipal(principal, _createdAt);

        // UUID version 7 per docs/10_DATABASE_SCHEMA.md (time-ordered ids).
        Assert.Equal(7, first.Id.Version);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void CreateFromPrincipal_rejects_service_account_principal()
    {
        var serviceAccount = new OidcPrincipal(PrincipalType.ServiceAccount, _issuer, _subject);

        Assert.Throws<ArgumentException>(() => UserProfile.CreateFromPrincipal(serviceAccount, _createdAt));
    }

    [Fact]
    public void Timestamps_are_normalized_to_utc()
    {
        var localCreatedAt = new DateTimeOffset(2026, 6, 11, 10, 0, 0, TimeSpan.FromHours(2));

        var profile = UserProfile.CreateFromPrincipal(CreateUserPrincipal(), localCreatedAt);

        Assert.Equal(TimeSpan.Zero, profile.CreatedAt.Offset);
        Assert.Equal(localCreatedAt.ToUniversalTime(), profile.CreatedAt);
    }

    [Fact]
    public void Profile_belongs_only_to_its_exact_oidc_identity()
    {
        var profile = UserProfile.CreateFromPrincipal(CreateUserPrincipal(), _createdAt);

        Assert.True(profile.BelongsToOidcIdentity(_issuer, _subject));
        Assert.False(profile.BelongsToOidcIdentity(_foreignIssuer, _subject));
        Assert.False(profile.BelongsToOidcIdentity(_issuer, _foreignSubject));
        Assert.False(profile.BelongsToOidcIdentity(_foreignIssuer, _foreignSubject));
    }

    [Fact]
    public void Same_subject_under_different_issuer_is_a_different_user()
    {
        // Subjects are unique only per issuer (threat T5): the same subject
        // value asserted by another provider must never address this profile.
        var profile = UserProfile.CreateFromPrincipal(CreateUserPrincipal(), _createdAt);
        var foreignPrincipal = CreateUserPrincipal(issuer: _foreignIssuer);

        Assert.False(profile.BelongsToPrincipal(foreignPrincipal));
    }

    [Fact]
    public void Identity_matching_is_case_sensitive()
    {
        var profile = UserProfile.CreateFromPrincipal(CreateUserPrincipal(), _createdAt);

        Assert.False(profile.BelongsToOidcIdentity(_issuer.ToUpperInvariant(), _subject));
        Assert.False(profile.BelongsToOidcIdentity(_issuer, _subject.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BelongsToOidcIdentity_returns_false_for_blank_values(string? blankValue)
    {
        var profile = UserProfile.CreateFromPrincipal(CreateUserPrincipal(), _createdAt);

        Assert.False(profile.BelongsToOidcIdentity(blankValue, _subject));
        Assert.False(profile.BelongsToOidcIdentity(_issuer, blankValue));
    }

    [Fact]
    public void RefreshMetadataFromPrincipal_mirrors_changed_metadata()
    {
        var profile = UserProfile.CreateFromPrincipal(
            CreateUserPrincipal(displayName: "Old Name", email: "old@example.test"),
            _createdAt);
        var refreshed = CreateUserPrincipal(displayName: "New Name", email: "new@example.test");

        profile.RefreshMetadataFromPrincipal(refreshed, _updatedAt);

        Assert.Equal("New Name", profile.DisplayName);
        Assert.Equal("new@example.test", profile.Email);
        Assert.Equal(_updatedAt, profile.UpdatedAt);
        Assert.Equal(_createdAt, profile.CreatedAt);
    }

    [Fact]
    public void RefreshMetadataFromPrincipal_clears_metadata_the_provider_no_longer_asserts()
    {
        // The profile is a cache of provider-asserted claims; dropped claims
        // must not leave stale personal data behind.
        var profile = UserProfile.CreateFromPrincipal(
            CreateUserPrincipal(displayName: "Casey Example", email: "casey@example.test"),
            _createdAt);

        profile.RefreshMetadataFromPrincipal(CreateUserPrincipal(), _updatedAt);

        Assert.Null(profile.DisplayName);
        Assert.Null(profile.Email);
    }

    [Fact]
    public void RefreshMetadataFromPrincipal_rejects_foreign_issuer()
    {
        var profile = UserProfile.CreateFromPrincipal(
            CreateUserPrincipal(displayName: "Casey Example"),
            _createdAt);
        var foreignPrincipal = CreateUserPrincipal(issuer: _foreignIssuer, displayName: "Intruder");

        Assert.Throws<ArgumentException>(
            () => profile.RefreshMetadataFromPrincipal(foreignPrincipal, _updatedAt));

        // The profile is untouched after the rejected refresh.
        Assert.Equal("Casey Example", profile.DisplayName);
        Assert.Equal(_createdAt, profile.UpdatedAt);
    }

    [Fact]
    public void RefreshMetadataFromPrincipal_rejects_foreign_subject()
    {
        var profile = UserProfile.CreateFromPrincipal(
            CreateUserPrincipal(displayName: "Casey Example"),
            _createdAt);
        var foreignPrincipal = CreateUserPrincipal(subjectId: _foreignSubject, displayName: "Intruder");

        Assert.Throws<ArgumentException>(
            () => profile.RefreshMetadataFromPrincipal(foreignPrincipal, _updatedAt));

        Assert.Equal("Casey Example", profile.DisplayName);
        Assert.Equal(_createdAt, profile.UpdatedAt);
    }

    [Fact]
    public void RefreshMetadataFromPrincipal_rejects_service_account_principal()
    {
        var profile = UserProfile.CreateFromPrincipal(CreateUserPrincipal(), _createdAt);
        var serviceAccount = new OidcPrincipal(PrincipalType.ServiceAccount, _issuer, _subject);

        Assert.Throws<ArgumentException>(
            () => profile.RefreshMetadataFromPrincipal(serviceAccount, _updatedAt));
    }

    [Fact]
    public void MetadataMatchesPrincipal_detects_matching_and_changed_metadata()
    {
        var profile = UserProfile.CreateFromPrincipal(
            CreateUserPrincipal(displayName: "Casey Example", email: "casey@example.test"),
            _createdAt);

        Assert.True(profile.MetadataMatchesPrincipal(
            CreateUserPrincipal(displayName: "Casey Example", email: "casey@example.test")));
        Assert.False(profile.MetadataMatchesPrincipal(
            CreateUserPrincipal(displayName: "Other Name", email: "casey@example.test")));
        Assert.False(profile.MetadataMatchesPrincipal(
            CreateUserPrincipal(displayName: "Casey Example")));
    }

    [Fact]
    public void ToString_exposes_identifiers_but_not_personal_metadata()
    {
        // Log-safety (threat T7): structured logs may carry identifiers, but
        // never display names or email addresses.
        var profile = UserProfile.CreateFromPrincipal(
            CreateUserPrincipal(displayName: "Casey Example", email: "casey@example.test"),
            _createdAt);

        var text = profile.ToString();

        Assert.Contains(_subject, text, StringComparison.Ordinal);
        Assert.Contains(_issuer, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Casey Example", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("casey@example.test", text, StringComparison.OrdinalIgnoreCase);
    }
}
