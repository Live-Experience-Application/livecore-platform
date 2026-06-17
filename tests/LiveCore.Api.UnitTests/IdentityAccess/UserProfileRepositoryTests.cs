// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.IdentityAccess;

/// <summary>
/// Integration-style tests for the EF Core-backed
/// <see cref="UserProfileRepository"/> (CORE-ID-002).
///
/// They run against an in-memory SQLite database so the real model mapping,
/// SQL translation and the unique (issuer, subject_id) index are exercised
/// on every test run without any database server or Docker. The behaviors
/// under test (exact equality lookups, unique index enforcement) are
/// relational semantics shared with PostgreSQL; provider-specific
/// verification happens against PostgreSQL in the deployment pipeline
/// (livecore-deploy) and the isolation test story CORE-ID-006.
/// </summary>
public sealed class UserProfileRepositoryTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _foreignIssuer = "https://id.foreign.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public UserProfileRepositoryTests()
    {
        // One open connection per test keeps the private in-memory database
        // alive while every step still uses its own context, so reads
        // genuinely round-trip through the database.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext() => new(_contextOptions);

    private static OidcPrincipal CreateUserPrincipal(
        string issuer = _issuer,
        string subjectId = _subject,
        string? displayName = null,
        string? email = null)
        => new(PrincipalType.User, issuer, subjectId, displayName, email);

    private async Task<UserProfile> SeedProfileAsync(OidcPrincipal principal)
    {
        var profile = UserProfile.CreateFromPrincipal(principal, _createdAt);
        await using var context = CreateContext();
        var repository = new UserProfileRepository(context);
        var result = await repository.AddAsync(profile, CancellationToken.None);
        Assert.Equal(UserProfileAddResult.Added, result);
        return profile;
    }

    [Fact]
    public async Task Profile_round_trips_through_the_database()
    {
        var seeded = await SeedProfileAsync(
            CreateUserPrincipal(displayName: "Casey Example", email: "casey@example.test"));

        await using var context = CreateContext();
        var repository = new UserProfileRepository(context);
        var loaded = await repository.FindByOidcIdentityAsync(_issuer, _subject, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(_issuer, loaded.Issuer);
        Assert.Equal(_subject, loaded.SubjectId);
        Assert.Equal("Casey Example", loaded.DisplayName);
        Assert.Equal("casey@example.test", loaded.Email);
        Assert.Equal(seeded.CreatedAt, loaded.CreatedAt);
        Assert.Equal(seeded.UpdatedAt, loaded.UpdatedAt);
    }

    [Fact]
    public async Task Lookup_with_foreign_issuer_returns_no_profile()
    {
        // Negative foreign-tenant case (threat T5): the same subject under
        // a different issuer is a different user; the lookup must not
        // return the stored profile.
        await SeedProfileAsync(CreateUserPrincipal());

        await using var context = CreateContext();
        var repository = new UserProfileRepository(context);
        var loaded = await repository.FindByOidcIdentityAsync(_foreignIssuer, _subject, CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Lookup_with_foreign_subject_returns_no_profile()
    {
        await SeedProfileAsync(CreateUserPrincipal());

        await using var context = CreateContext();
        var repository = new UserProfileRepository(context);
        var loaded = await repository.FindByOidcIdentityAsync(
            _issuer,
            "11111111-2222-3333-4444-555555555555",
            CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Lookup_is_case_sensitive_for_issuer_and_subject()
    {
        // Case variance must never address a foreign profile (threat T5).
        await SeedProfileAsync(CreateUserPrincipal());

        await using var context = CreateContext();
        var repository = new UserProfileRepository(context);

        Assert.Null(await repository.FindByOidcIdentityAsync(
            _issuer.ToUpperInvariant(), _subject, CancellationToken.None));
        Assert.Null(await repository.FindByOidcIdentityAsync(
            _issuer, _subject.ToUpperInvariant(), CancellationToken.None));
    }

    [Theory]
    [InlineData("not-an-absolute-uri", _subject)]
    [InlineData("   ", _subject)]
    [InlineData(_issuer, "   ")]
    public async Task Lookup_rejects_invalid_identity_values(string issuer, string subjectId)
    {
        await using var context = CreateContext();
        var repository = new UserProfileRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByOidcIdentityAsync(issuer, subjectId, CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_identity_is_rejected_and_the_existing_profile_stays_unchanged()
    {
        // The unique index on (issuer, subject_id) is the database-level
        // guarantee that a second writer can never overwrite an existing
        // profile with its own row.
        var existing = await SeedProfileAsync(CreateUserPrincipal(displayName: "First Writer"));
        var duplicate = UserProfile.CreateFromPrincipal(
            CreateUserPrincipal(displayName: "Second Writer"),
            _createdAt);

        await using (var context = CreateContext())
        {
            var repository = new UserProfileRepository(context);
            var result = await repository.AddAsync(duplicate, CancellationToken.None);

            Assert.Equal(UserProfileAddResult.DuplicateIdentity, result);
        }

        await using (var context = CreateContext())
        {
            var repository = new UserProfileRepository(context);
            var loaded = await repository.FindByOidcIdentityAsync(_issuer, _subject, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(existing.Id, loaded.Id);
            Assert.Equal("First Writer", loaded.DisplayName);
        }
    }

    [Fact]
    public async Task Same_subject_under_different_issuers_creates_two_distinct_profiles()
    {
        var first = await SeedProfileAsync(CreateUserPrincipal());
        var second = await SeedProfileAsync(CreateUserPrincipal(issuer: _foreignIssuer));

        await using var context = CreateContext();
        var repository = new UserProfileRepository(context);
        var fromFirstIssuer = await repository.FindByOidcIdentityAsync(_issuer, _subject, CancellationToken.None);
        var fromForeignIssuer = await repository.FindByOidcIdentityAsync(
            _foreignIssuer, _subject, CancellationToken.None);

        Assert.NotNull(fromFirstIssuer);
        Assert.NotNull(fromForeignIssuer);
        Assert.Equal(first.Id, fromFirstIssuer.Id);
        Assert.Equal(second.Id, fromForeignIssuer.Id);
        Assert.NotEqual(fromFirstIssuer.Id, fromForeignIssuer.Id);
    }

    [Fact]
    public async Task Metadata_refresh_is_persisted()
    {
        await SeedProfileAsync(CreateUserPrincipal(displayName: "Old Name", email: "old@example.test"));

        await using (var context = CreateContext())
        {
            var repository = new UserProfileRepository(context);
            var loaded = await repository.FindByOidcIdentityAsync(_issuer, _subject, CancellationToken.None);
            Assert.NotNull(loaded);

            loaded.RefreshMetadataFromPrincipal(
                CreateUserPrincipal(displayName: "New Name"),
                _updatedAt);
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new UserProfileRepository(context);
            var reloaded = await repository.FindByOidcIdentityAsync(_issuer, _subject, CancellationToken.None);

            Assert.NotNull(reloaded);
            Assert.Equal("New Name", reloaded.DisplayName);
            Assert.Null(reloaded.Email);
            Assert.Equal(_updatedAt, reloaded.UpdatedAt);
            Assert.Equal(_createdAt, reloaded.CreatedAt);
        }
    }
}
