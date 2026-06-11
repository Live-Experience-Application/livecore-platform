using System.Text;
using LiveCore.Api.Workspaces;

namespace LiveCore.Api.UnitTests.Workspaces;

/// <summary>
/// Unit tests for <see cref="WorkspaceInvitationToken"/> (CORE-WS-004): the
/// scoped invite token's generation and hashing. These prove the security
/// promises of the secret (threat T6 "Invite abuse", threat T7 "Log leaks" in
/// docs/07_SECURITY_THREAT_MODEL.md): high-entropy random generation, a one-way
/// non-reversible stored hash, URL-safe opaque plaintext, and constant-time hash
/// comparison.
/// </summary>
public class WorkspaceInvitationTokenTests
{
    [Fact]
    public void Generate_produces_at_least_128_bits_of_entropy()
    {
        // 32 random bytes = 256 bits, well above the 128-bit floor (threat T6).
        Assert.True(WorkspaceInvitationToken.TokenByteLength >= 16);

        var token = WorkspaceInvitationToken.Generate();
        // base64url (no padding) of 32 bytes is 43 characters; decoding back must
        // yield exactly 32 bytes of entropy.
        var decoded = DecodeBase64Url(token);
        Assert.Equal(WorkspaceInvitationToken.TokenByteLength, decoded.Length);
    }

    [Fact]
    public void Generate_is_url_safe_and_has_no_padding()
    {
        var token = WorkspaceInvitationToken.Generate();

        // base64url uses '-' and '_' instead of '+' and '/', and drops '='
        // padding, so the plaintext can travel in a link/header unescaped.
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }

    [Fact]
    public void Generate_yields_distinct_unguessable_tokens()
    {
        // A cryptographically secure RNG must not repeat across many draws.
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(tokens.Add(WorkspaceInvitationToken.Generate()));
        }
    }

    [Fact]
    public void Hash_is_deterministic_for_the_same_input()
    {
        var token = WorkspaceInvitationToken.Generate();

        // The same plaintext always hashes to the same value, so a presented
        // token can be matched against the stored hash.
        Assert.Equal(WorkspaceInvitationToken.Hash(token), WorkspaceInvitationToken.Hash(token));
    }

    [Fact]
    public void Hash_differs_for_different_inputs()
    {
        Assert.NotEqual(
            WorkspaceInvitationToken.Hash("token-a"),
            WorkspaceInvitationToken.Hash("token-b"));
    }

    [Fact]
    public void Hash_is_not_the_plaintext_and_is_not_reversible()
    {
        // T6/T7: the stored hash is a one-way reduction. It is not the plaintext,
        // and it decodes to a 32-byte SHA-256 digest, not to the original token
        // bytes — the secret cannot be recovered from the stored value.
        var token = WorkspaceInvitationToken.Generate();
        var hash = WorkspaceInvitationToken.Hash(token);

        Assert.NotEqual(token, hash);
        var digest = Convert.FromBase64String(hash);
        Assert.Equal(WorkspaceInvitationToken.HashByteLength, digest.Length);
        // The digest bytes are not the token's own bytes (no trivial encoding).
        Assert.NotEqual(Convert.ToBase64String(DecodeBase64Url(token)), hash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Hash_rejects_a_blank_token(string? token)
        => Assert.Throws<ArgumentException>(() => WorkspaceInvitationToken.Hash(token!));

    [Fact]
    public void IsValidHash_accepts_a_real_hash_and_rejects_other_values()
    {
        var hash = WorkspaceInvitationToken.Hash(WorkspaceInvitationToken.Generate());

        Assert.True(WorkspaceInvitationToken.IsValidHash(hash));
        Assert.False(WorkspaceInvitationToken.IsValidHash(null));
        Assert.False(WorkspaceInvitationToken.IsValidHash(""));
        Assert.False(WorkspaceInvitationToken.IsValidHash("not-base64!!"));
        // A valid base64 string of the wrong length is not a SHA-256 digest.
        Assert.False(WorkspaceInvitationToken.IsValidHash(Convert.ToBase64String(new byte[16])));
    }

    [Fact]
    public void HashesEqual_compares_equal_and_unequal_hashes()
    {
        var hash = WorkspaceInvitationToken.Hash("same");

        Assert.True(WorkspaceInvitationToken.HashesEqual(hash, WorkspaceInvitationToken.Hash("same")));
        Assert.False(WorkspaceInvitationToken.HashesEqual(hash, WorkspaceInvitationToken.Hash("different")));
        Assert.False(WorkspaceInvitationToken.HashesEqual(hash, null));
        Assert.False(WorkspaceInvitationToken.HashesEqual(null, hash));
        Assert.False(WorkspaceInvitationToken.HashesEqual(null, null));
    }

    // Decodes a base64url (no padding) string back to bytes for entropy checks.
    private static byte[] DecodeBase64Url(string value)
    {
        var builder = new StringBuilder(value.Replace('-', '+').Replace('_', '/'));
        while (builder.Length % 4 != 0)
        {
            builder.Append('=');
        }

        return Convert.FromBase64String(builder.ToString());
    }
}
