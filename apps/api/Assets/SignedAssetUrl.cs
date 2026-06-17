// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Assets;

/// <summary>
/// A short-lived, signed URL that grants ONE time-bounded access to a private asset object (CORE-AST-002,
/// the storage adapter interface). It is the only thing the storage adapter (<see cref="IAssetStorage"/>)
/// ever hands back: an <see cref="Asset"/>'s bytes live in a private, S3-compatible bucket
/// (docs/12_STORAGE_ASSETS.md; ADR 0006) and are reachable ONLY through such a signed, expiring URL after
/// a server-side permission check — the epic acceptance criterion ("Assets are private by default and
/// accessed only through authorized signed URLs") and threat T4 "Asset leak" in
/// docs/07_SECURITY_THREAT_MODEL.md ("private buckets only; signed URL after authorization; no public
/// object listing; short-lived URLs").
///
/// The security invariants of a signed URL are enforced by this TYPE so they cannot be forgotten by any
/// adapter implementation: a <see cref="SignedAssetUrl"/> CANNOT be constructed without
/// <list type="bullet">
///   <item>an absolute URL (a relative or empty URL is rejected — there is no "public path" affordance), and</item>
///   <item>
///   a strictly positive lifetime that is at most <see cref="MaxLifetime"/>, so the URL always carries a
///   near-future <see cref="ExpiresAt"/> and can never be long-lived or non-expiring
///   (docs/12_STORAGE_ASSETS.md "signed URLs are short-lived"; "Avoid long-lived signed URLs"). A
///   would-be permanent or public URL is therefore unrepresentable.
///   </item>
/// </list>
///
/// The URL string itself is a SECRET: it embeds the object key and the signature, so possessing it is
/// equivalent to being granted the access it authorizes (until it expires). It must never be written to
/// logs (threats T4/T7), so <see cref="ToString"/> deliberately EXCLUDES the URL and reveals only the
/// operation and the expiry — exactly as <see cref="Asset.ToString"/> excludes the storage coordinates.
/// </summary>
public sealed class SignedAssetUrl
{
    /// <summary>
    /// The maximum lifetime a signed asset URL may have. Signed URLs are short-lived
    /// (docs/12_STORAGE_ASSETS.md "signed URLs are short-lived"; "Avoid long-lived signed URLs"); one hour
    /// is a generous ceiling that still comfortably accommodates a large upload while rejecting a
    /// multi-day or effectively permanent URL. A conforming adapter chooses a lifetime within this bound
    /// (typically much shorter); this is the absolute upper limit the type will accept, not a default.
    /// </summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(1);

    private SignedAssetUrl(Uri url, AssetStorageOperation operation, DateTimeOffset expiresAt)
    {
        Url = url;
        Operation = operation;
        // Normalize the expiry to UTC so the value is offset-independent (mirrors Asset timestamps).
        ExpiresAt = expiresAt.ToUniversalTime();
    }

    /// <summary>
    /// The signed URL. Absolute by construction; it embeds the object key and the signature and is a
    /// SECRET — never log it (threats T4/T7).
    /// </summary>
    public Uri Url { get; }

    /// <summary>The operation this URL authorizes (<see cref="AssetStorageOperation.Upload"/> or <see cref="AssetStorageOperation.Download"/>).</summary>
    public AssetStorageOperation Operation { get; }

    /// <summary>
    /// When the URL stops being valid (UTC). Always present and always in the near future at creation
    /// (within <see cref="MaxLifetime"/>): a signed asset URL is short-lived by construction.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>
    /// Mints a signed URL for the given <paramref name="operation"/>, valid for <paramref name="lifetime"/>
    /// starting at <paramref name="issuedAt"/>. The expiry is <c>issuedAt + lifetime</c>. The lifetime must
    /// be strictly positive and at most <see cref="MaxLifetime"/>, so the result is always short-lived
    /// (docs/12_STORAGE_ASSETS.md); the URL must be absolute (there is no public/relative path affordance).
    /// </summary>
    /// <param name="url">The absolute signed URL (embeds the object key and signature).</param>
    /// <param name="operation">The operation the URL authorizes.</param>
    /// <param name="issuedAt">When the URL was minted (its validity starts here).</param>
    /// <param name="lifetime">How long the URL stays valid; must be &gt; 0 and &lt;= <see cref="MaxLifetime"/>.</param>
    /// <exception cref="ArgumentNullException">The URL is null.</exception>
    /// <exception cref="ArgumentException">The URL is not absolute.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The operation is not a defined value, or the lifetime is not strictly positive and within
    /// <see cref="MaxLifetime"/>.
    /// </exception>
    public static SignedAssetUrl Create(
        Uri url,
        AssetStorageOperation operation,
        DateTimeOffset issuedAt,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(url);

        // An absolute URL is required: a signed asset URL points at the private storage endpoint and
        // carries its own query signature, so a relative path (which would resolve against some other host)
        // is never a valid signed URL.
        if (!url.IsAbsoluteUri)
        {
            throw new ArgumentException("A signed asset URL must be an absolute URL.", nameof(url));
        }

        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Operation is not a defined asset storage operation.");
        }

        // A signed URL must be short-lived: a non-positive lifetime is meaningless, and a lifetime beyond
        // the ceiling would make the URL effectively long-lived (threat T4; docs/12_STORAGE_ASSETS.md).
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "A signed asset URL lifetime must be strictly positive.");
        }

        if (lifetime > MaxLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                lifetime,
                $"A signed asset URL must be short-lived; the lifetime must not exceed {MaxLifetime}.");
        }

        return new SignedAssetUrl(url, operation, issuedAt + lifetime);
    }

    /// <summary>
    /// Whether this URL has expired as of the given instant. A signed URL is invalid once
    /// <see cref="ExpiresAt"/> has been reached or passed; callers and the storage backend both treat an
    /// expired URL as no access.
    /// </summary>
    public bool IsExpired(DateTimeOffset asOf) => asOf >= ExpiresAt;

    /// <summary>
    /// Log-safe representation: the operation and the expiry only. The signed <see cref="Url"/> is a secret
    /// (it embeds the object key and signature and grants the access until it expires), so it is
    /// deliberately EXCLUDED — a leaked log line must never be replayable as asset access (threats T4
    /// "Asset leak" and T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString() => $"SignedAssetUrl operation={Operation} expiresAt={ExpiresAt:O}";
}
