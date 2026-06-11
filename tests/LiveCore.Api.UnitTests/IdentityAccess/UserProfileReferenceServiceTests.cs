using LiveCore.Api.IdentityAccess;

namespace LiveCore.Api.UnitTests.IdentityAccess;

/// <summary>
/// Unit tests for <see cref="UserProfileReferenceService"/> (CORE-ID-002):
/// one profile per OIDC identity pair, metadata mirroring, create-race
/// adoption, and the negative cases where a foreign identity (same subject
/// under another issuer) must never receive or modify an existing profile
/// (threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public class UserProfileReferenceServiceTests
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _foreignIssuer = "https://id.foreign.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";

    private static readonly DateTimeOffset _initialTime = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _laterTime = new(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);

    private readonly FakeUserProfileRepository _repository = new();
    private readonly FixedTimeProvider _timeProvider = new(_initialTime);
    private readonly UserProfileReferenceService _service;

    public UserProfileReferenceServiceTests()
    {
        _service = new UserProfileReferenceService(_repository, _timeProvider);
    }

    private static OidcPrincipal CreateUserPrincipal(
        string issuer = _issuer,
        string subjectId = _subject,
        string? displayName = null,
        string? email = null)
        => new(PrincipalType.User, issuer, subjectId, displayName, email);

    [Fact]
    public async Task Ensure_creates_profile_on_first_sight()
    {
        var principal = CreateUserPrincipal(displayName: "Casey Example", email: "casey@example.test");

        var profile = await _service.EnsureUserProfileAsync(principal, CancellationToken.None);

        var stored = Assert.Single(_repository.Profiles);
        Assert.Same(profile, stored);
        Assert.True(profile.BelongsToPrincipal(principal));
        Assert.Equal("Casey Example", profile.DisplayName);
        Assert.Equal("casey@example.test", profile.Email);
        Assert.Equal(_initialTime, profile.CreatedAt);
    }

    [Fact]
    public async Task Ensure_returns_existing_profile_without_creating_a_second_one()
    {
        var principal = CreateUserPrincipal(displayName: "Casey Example");
        var first = await _service.EnsureUserProfileAsync(principal, CancellationToken.None);

        var second = await _service.EnsureUserProfileAsync(principal, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_repository.Profiles);
    }

    [Fact]
    public async Task Ensure_does_not_write_when_metadata_is_unchanged()
    {
        var principal = CreateUserPrincipal(displayName: "Casey Example");
        await _service.EnsureUserProfileAsync(principal, CancellationToken.None);

        await _service.EnsureUserProfileAsync(principal, CancellationToken.None);

        Assert.Equal(0, _repository.UpdateCallCount);
    }

    [Fact]
    public async Task Ensure_refreshes_changed_metadata_on_later_sight()
    {
        await _service.EnsureUserProfileAsync(
            CreateUserPrincipal(displayName: "Old Name", email: "old@example.test"),
            CancellationToken.None);
        _timeProvider.UtcNow = _laterTime;

        var profile = await _service.EnsureUserProfileAsync(
            CreateUserPrincipal(displayName: "New Name", email: "new@example.test"),
            CancellationToken.None);

        Assert.Equal("New Name", profile.DisplayName);
        Assert.Equal("new@example.test", profile.Email);
        Assert.Equal(_laterTime, profile.UpdatedAt);
        Assert.Equal(1, _repository.UpdateCallCount);
        Assert.Single(_repository.Profiles);
    }

    [Fact]
    public async Task Ensure_clears_metadata_the_provider_no_longer_asserts()
    {
        await _service.EnsureUserProfileAsync(
            CreateUserPrincipal(displayName: "Casey Example", email: "casey@example.test"),
            CancellationToken.None);

        var profile = await _service.EnsureUserProfileAsync(CreateUserPrincipal(), CancellationToken.None);

        Assert.Null(profile.DisplayName);
        Assert.Null(profile.Email);
    }

    [Fact]
    public async Task Ensure_creates_a_distinct_profile_for_the_same_subject_under_a_foreign_issuer()
    {
        // Negative foreign-tenant case (threat T5): the same subject value
        // asserted by a different issuer is a different user. It must get
        // its own profile and must never receive the existing one.
        var existing = await _service.EnsureUserProfileAsync(
            CreateUserPrincipal(displayName: "Casey Example"),
            CancellationToken.None);
        _timeProvider.UtcNow = _laterTime;

        var foreign = await _service.EnsureUserProfileAsync(
            CreateUserPrincipal(issuer: _foreignIssuer, displayName: "Foreign Name"),
            CancellationToken.None);

        Assert.NotEqual(existing.Id, foreign.Id);
        Assert.Equal(2, _repository.Profiles.Count);
        // The existing profile is untouched by the foreign request.
        Assert.Equal("Casey Example", existing.DisplayName);
        Assert.Equal(_initialTime, existing.UpdatedAt);
        Assert.Equal(0, _repository.UpdateCallCount);
    }

    [Fact]
    public async Task Ensure_creates_a_distinct_profile_for_a_foreign_subject_under_the_same_issuer()
    {
        var existing = await _service.EnsureUserProfileAsync(CreateUserPrincipal(), CancellationToken.None);

        var foreign = await _service.EnsureUserProfileAsync(
            CreateUserPrincipal(subjectId: "11111111-2222-3333-4444-555555555555"),
            CancellationToken.None);

        Assert.NotEqual(existing.Id, foreign.Id);
        Assert.Equal(2, _repository.Profiles.Count);
    }

    [Fact]
    public async Task Ensure_rejects_service_account_principals()
    {
        var serviceAccount = new OidcPrincipal(PrincipalType.ServiceAccount, _issuer, _subject);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.EnsureUserProfileAsync(serviceAccount, CancellationToken.None));

        Assert.Empty(_repository.Profiles);
    }

    [Fact]
    public async Task Ensure_adopts_the_winning_profile_after_losing_the_create_race()
    {
        // Two concurrent first requests of the same user: the loser's insert
        // hits the unique index and must adopt the winner's row instead of
        // failing or duplicating.
        var winner = UserProfile.CreateFromPrincipal(
            CreateUserPrincipal(displayName: "Winner Name"),
            _initialTime);
        _repository.SimulateDuplicateOnAdd = true;
        _repository.ProfileAppearingAfterDuplicate = winner;

        var profile = await _service.EnsureUserProfileAsync(
            CreateUserPrincipal(displayName: "Winner Name"),
            CancellationToken.None);

        Assert.Equal(winner.Id, profile.Id);
        Assert.Single(_repository.Profiles);
    }

    [Fact]
    public async Task Ensure_refreshes_the_winning_profile_when_its_metadata_is_stale()
    {
        var winner = UserProfile.CreateFromPrincipal(
            CreateUserPrincipal(displayName: "Stale Name"),
            _initialTime);
        _repository.SimulateDuplicateOnAdd = true;
        _repository.ProfileAppearingAfterDuplicate = winner;

        var profile = await _service.EnsureUserProfileAsync(
            CreateUserPrincipal(displayName: "Fresh Name"),
            CancellationToken.None);

        Assert.Equal(winner.Id, profile.Id);
        Assert.Equal("Fresh Name", profile.DisplayName);
        Assert.Equal(1, _repository.UpdateCallCount);
    }

    [Fact]
    public async Task Ensure_fails_closed_when_duplicate_is_reported_but_no_profile_exists()
    {
        _repository.SimulateDuplicateOnAdd = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.EnsureUserProfileAsync(CreateUserPrincipal(), CancellationToken.None));
    }

    /// <summary>
    /// In-memory repository fake. <see cref="SimulateDuplicateOnAdd"/>
    /// models the unique-index rejection;
    /// <see cref="ProfileAppearingAfterDuplicate"/> models the concurrent
    /// winner whose row becomes visible after the lost create race.
    /// </summary>
    private sealed class FakeUserProfileRepository : IUserProfileRepository
    {
        public List<UserProfile> Profiles { get; } = [];

        public int UpdateCallCount { get; private set; }

        public bool SimulateDuplicateOnAdd { get; set; }

        public UserProfile? ProfileAppearingAfterDuplicate { get; set; }

        public Task<UserProfile?> FindByOidcIdentityAsync(
            string issuer,
            string subjectId,
            CancellationToken cancellationToken)
            => Task.FromResult(Profiles.FirstOrDefault(profile => profile.BelongsToOidcIdentity(issuer, subjectId)));

        public Task<UserProfileAddResult> AddAsync(UserProfile profile, CancellationToken cancellationToken)
        {
            if (SimulateDuplicateOnAdd)
            {
                if (ProfileAppearingAfterDuplicate is not null)
                {
                    Profiles.Add(ProfileAppearingAfterDuplicate);
                    ProfileAppearingAfterDuplicate = null;
                }

                return Task.FromResult(UserProfileAddResult.DuplicateIdentity);
            }

            Profiles.Add(profile);
            return Task.FromResult(UserProfileAddResult.Added);
        }

        public Task UpdateAsync(UserProfile profile, CancellationToken cancellationToken)
        {
            UpdateCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
