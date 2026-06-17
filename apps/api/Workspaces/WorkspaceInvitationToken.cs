// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Security.Cryptography;
using System.Text;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// The scoped invite token model of the Workspaces module (CORE-WS-004): the
/// security core of the workspace member invite placeholder.
///
/// A workspace invite token is a credential-like SECRET that grants a single,
/// scoped, time-bound right to join one workspace with one role (threat T6
/// "Invite abuse" in docs/07_SECURITY_THREAT_MODEL.md: "scoped invite tokens,
/// expiry, revocation, role limitation"). It is NOT authentication: it never
/// logs a subject in and is never a password — authentication is owned by the
/// external OIDC provider and this platform implements no custom password auth
/// (docs/adr/0005-oidc-first-authentication.md). An invite token is therefore a
/// one-time scoped GRANT to be admitted to a workspace, distinct from the OIDC
/// access token the caller authenticates with; it is plain high-entropy random
/// bytes, never a JWT and never validated as one.
///
/// This type is the single place that knows how the secret is generated and how
/// it is reduced to the value persisted at rest. It guarantees the two
/// non-negotiable handling rules (threats T6/T7):
/// <list type="bullet">
///   <item>
///   GENERATION uses a cryptographically secure RNG
///   (<see cref="RandomNumberGenerator"/>) with <see cref="TokenByteLength"/> =
///   32 bytes (256 bits) of entropy — comfortably above the 128-bit floor — so
///   the plaintext is computationally infeasible to guess or brute-force.
///   </item>
///   <item>
///   STORAGE is only ever the SHA-256 <em>hash</em> of the plaintext, encoded
///   as base64. The plaintext is returned to the caller exactly once, at
///   creation, and is never stored, never re-derivable from the hash and never
///   logged. The hash is a one-way reduction: possessing it does not yield the
///   token, so a leaked database row cannot be replayed as an invite (threats
///   T6/T7).
///   </item>
/// </list>
///
/// The token is opaque and URL-safe: the plaintext is base64url (RFC 4648
/// §5, no padding) so it can travel in a link or header without escaping. The
/// scope (organization, workspace, role) and the expiry live on the
/// <see cref="WorkspaceInvitation"/> aggregate, not in the token bytes: the
/// token is just an unguessable lookup secret, and every redemption re-checks
/// the scope and expiry server-side rather than trusting anything encoded in the
/// secret (threat T6 role limitation).
/// </summary>
public static class WorkspaceInvitationToken
{
    /// <summary>
    /// Entropy of the generated plaintext token in bytes. 32 bytes is 256 bits,
    /// well above the 128-bit minimum for an unguessable single-use secret
    /// (threat T6).
    /// </summary>
    public const int TokenByteLength = 32;

    /// <summary>
    /// Length of the stored SHA-256 hash in bytes. SHA-256 produces a 32-byte
    /// digest regardless of input length.
    /// </summary>
    public const int HashByteLength = 32;

    /// <summary>
    /// Generates a new cryptographically secure plaintext invite token. The
    /// caller hashes it with <see cref="Hash"/> for storage and returns the
    /// plaintext to the inviter exactly once; it is never persisted and never
    /// logged (threats T6/T7).
    /// </summary>
    /// <returns>
    /// A URL-safe base64url (no padding) string of <see cref="TokenByteLength"/>
    /// random bytes.
    /// </returns>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenByteLength);
        return ToBase64Url(bytes);
    }

    /// <summary>
    /// Computes the SHA-256 hash of a plaintext token, encoded as base64, for
    /// storage and for tenant-scoped lookup. The hash is a one-way reduction:
    /// the same plaintext always hashes to the same value (so a presented token
    /// can be matched against the stored hash), but the plaintext can never be
    /// recovered from the hash (so the stored value is not a usable secret;
    /// threats T6/T7). Comparing presented-token hashes against the stored hash
    /// must use <see cref="HashesEqual"/> so the match is constant-time.
    /// </summary>
    /// <param name="token">The plaintext token to hash.</param>
    /// <returns>The base64-encoded SHA-256 digest of the token.</returns>
    /// <exception cref="ArgumentException">The token is null or blank.</exception>
    public static string Hash(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("A token value is required to hash.", nameof(token));
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(digest);
    }

    /// <summary>
    /// Whether the given value is a syntactically valid stored token hash: a
    /// non-blank base64 string that decodes to exactly
    /// <see cref="HashByteLength"/> bytes (a SHA-256 digest). Used by the
    /// aggregate to reject a malformed hash before it is ever persisted, so a
    /// non-hash value can never masquerade as the stored secret reduction.
    /// </summary>
    public static bool IsValidHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[HashByteLength];
        return Convert.TryFromBase64String(value, buffer, out var written)
            && written == HashByteLength;
    }

    /// <summary>
    /// Constant-time equality of two stored token hashes. Comparing the hash of
    /// a presented token against the stored hash with a constant-time comparison
    /// avoids leaking, through timing, how much of a guessed token was correct
    /// (defence in depth for the single-use secret; threat T6). Two null/blank
    /// values are never equal.
    /// </summary>
    public static bool HashesEqual(string? storedHash, string? presentedHash)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(presentedHash))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(storedHash),
            Encoding.UTF8.GetBytes(presentedHash));
    }

    // Base64url (RFC 4648 §5) without padding: URL- and header-safe so the
    // plaintext token can travel in a link without escaping. This encodes the
    // RANDOM PLAINTEXT only; the stored hash uses standard base64 because it
    // never appears in a URL.
    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
