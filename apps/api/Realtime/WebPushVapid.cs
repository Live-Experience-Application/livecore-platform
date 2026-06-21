// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Builds the VAPID (RFC 8292) application-server <c>Authorization</c> header that authenticates a Web Push to a
/// push service (CORE-PUSH-002). VAPID signs a short-lived ES256 (ECDSA P-256 + SHA-256) JWT with the deployment's
/// application-server key pair; the push service verifies it against the public key the browser subscribed with.
///
/// IMPLEMENTED ENTIRELY ON THE BCL — <see cref="ECDsa"/> for the P-256 signature and
/// <see cref="System.Buffers.Text.Base64Url"/>-style encoding done locally — so the outbound push needs NO new
/// dependency (the dependency policy in AGENTS.md). The default sender (<see cref="VapidWebPushSender"/>) delivers a
/// PAYLOAD-LESS push, so no message-encryption crypto is involved at all — the strongest content-free guarantee
/// (nothing, not even an encrypted blob, reaches the device); only this authentication JWT is produced.
///
/// THE PRIVATE KEY is supplied by the caller from configuration only (<see cref="WebPushDeliveryOptions"/>); it is
/// never hardcoded and never logged (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). The values here are generic
/// transport crypto material; nothing carries vertical domain language (AGENTS.md).
/// </summary>
internal static class WebPushVapid
{
    // The VAPID/Web Push key is a P-256 point: an uncompressed SEC1 point (0x04 || X(32) || Y(32)) is 65 bytes,
    // and the raw private scalar is 32 bytes. A value of any other length cannot be a P-256 key.
    private const int _uncompressedPointLength = 65;
    private const int _coordinateLength = 32;
    private const byte _uncompressedPointPrefix = 0x04;

    /// <summary>
    /// Builds the ECDSA P-256 signing key from the configured base64url VAPID public point and private scalar. Both
    /// are required: the public point gives the curve coordinates and the private scalar the signing key, together a
    /// complete key the BCL can sign with. A malformed key (wrong length or a non-uncompressed point) is rejected so
    /// a misconfiguration fails loudly rather than producing an unverifiable signature.
    /// </summary>
    /// <exception cref="ArgumentException">A key is blank or not a well-formed P-256 key.</exception>
    public static ECDsa CreateSigningKey(string publicKeyBase64Url, string privateKeyBase64Url)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64Url))
        {
            throw new ArgumentException("The VAPID public key must not be blank.", nameof(publicKeyBase64Url));
        }

        if (string.IsNullOrWhiteSpace(privateKeyBase64Url))
        {
            throw new ArgumentException("The VAPID private key must not be blank.", nameof(privateKeyBase64Url));
        }

        var point = Base64UrlDecode(publicKeyBase64Url);
        if (point.Length != _uncompressedPointLength || point[0] != _uncompressedPointPrefix)
        {
            throw new ArgumentException(
                "The VAPID public key must be a base64url-encoded uncompressed P-256 point (65 bytes).",
                nameof(publicKeyBase64Url));
        }

        var privateScalar = Base64UrlDecode(privateKeyBase64Url);
        if (privateScalar.Length != _coordinateLength)
        {
            throw new ArgumentException(
                "The VAPID private key must be a base64url-encoded 32-byte P-256 scalar.",
                nameof(privateKeyBase64Url));
        }

        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = point[1..(1 + _coordinateLength)],
                Y = point[(1 + _coordinateLength)..],
            },
            D = privateScalar,
        };

        return ECDsa.Create(parameters);
    }

    /// <summary>
    /// Builds the <c>Authorization: vapid t=&lt;jwt&gt;, k=&lt;publicKey&gt;</c> header value (RFC 8292) for a push
    /// to <paramref name="endpoint"/>. The JWT's <c>aud</c> is the push service origin, <c>sub</c> the configured
    /// contact, and <c>exp</c> the supplied expiry. The signature is ES256 (raw r||s, the JWT/IEEE-P1363 form
    /// <see cref="ECDsa.SignData(byte[], HashAlgorithmName)"/> produces by default).
    /// </summary>
    public static string CreateAuthorizationHeader(
        ECDsa signingKey,
        string publicKeyBase64Url,
        string endpoint,
        string subject,
        DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(signingKey);

        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["aud"] = AudienceFor(endpoint),
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
            ["sub"] = subject,
        };
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims));

        var signingInput = string.Concat(header, ".", payload);
        var signature = signingKey.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        var token = string.Concat(signingInput, ".", Base64UrlEncode(signature));

        return string.Concat("vapid t=", token, ", k=", publicKeyBase64Url);
    }

    /// <summary>The push-service ORIGIN (scheme + authority) of an endpoint — the VAPID <c>aud</c> claim.</summary>
    public static string AudienceFor(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return new Uri(endpoint, UriKind.Absolute).GetLeftPart(UriPartial.Authority);
    }

    /// <summary>Encodes bytes as unpadded base64url (RFC 4648 §5), the JWT/Web Push encoding.</summary>
    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes an unpadded base64url string to bytes.</summary>
    /// <exception cref="FormatException">The value is not valid base64url.</exception>
    public static byte[] Base64UrlDecode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding == 2)
        {
            normalized += "==";
        }
        else if (padding == 3)
        {
            normalized += "=";
        }
        else if (padding == 1)
        {
            throw new FormatException("The value is not valid base64url.");
        }

        return Convert.FromBase64String(normalized);
    }
}
