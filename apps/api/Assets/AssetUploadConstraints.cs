// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Assets;

/// <summary>
/// The deployment's configurable upload-acceptance policy for the upload-intent flow (CORE-AST-007, the
/// "Audit Integrity and Security Hardening" epic) — a MIME content-type ALLOWLIST and an absolute per-object
/// SIZE CEILING. Both are abuse-surface hardening that is INDEPENDENT of the workspace storage quota
/// (CORE-MON-006): the quota bounds a workspace's TOTAL stored bytes, while this ceiling bounds a SINGLE
/// object's declared size, and the allowlist bounds which media types may ever be uploaded at all. Before this
/// policy the content type was a free-form token with no allowlist and there was no per-object ceiling beyond
/// the quota (docs/12_STORAGE_ASSETS.md).
///
/// FAIL-CLOSED, PRIVACY UNCHANGED. The <see cref="AssetUploadIntentService"/> evaluates this policy BEFORE it
/// opens its unit of work, so a disallowed content type or an over-ceiling object is rejected before any quota
/// is consumed and before any signed upload URL is minted (the story's acceptance criterion; threat T4 "Asset
/// leak"). Neither check reads or returns any storage coordinate, and the privacy model (private bucket, signed
/// URL after authorization) is unchanged — a rejected intent simply never reaches the storage adapter. The
/// values are plain, non-sensitive configuration (a list of generic MIME tokens and a byte count), never a
/// secret and never vertical vocabulary (threat T7; AGENTS.md).
///
/// CONFIGURABLE WITH SAFE DEFAULTS. <see cref="FromConfiguration"/> reads the policy under
/// <see cref="ConfigurationSection"/> (<c>Assets:Upload:AllowedContentTypes</c> /
/// <c>Assets:Upload:MaxObjectSizeBytes</c>), falling back to the curated <see cref="DefaultAllowedContentTypes"/>
/// and <see cref="DefaultMaxObjectSizeBytes"/> when a value is ABSENT — so an unconfigured deployment is already
/// hardened rather than wide open. The allowlist match is case-insensitive on the declared MIME token. An
/// explicitly EMPTY configured allowlist disables the content-type restriction (every well-formed token is
/// admitted); a blank/absent value never silently disables it. A configured ceiling that is not strictly
/// positive is rejected at startup rather than recording a degenerate ceiling.
/// </summary>
internal sealed class AssetUploadConstraints
{
    /// <summary>Configuration section the upload-acceptance policy is read from (<c>Assets:Upload</c>).</summary>
    public const string ConfigurationSection = "Assets:Upload";

    /// <summary>
    /// The curated default MIME allowlist when none is configured: the common, broadly safe media types a
    /// generic live-experience platform stores (images, documents, audio and video, plain text). A deployment
    /// overrides it under <c>Assets:Upload:AllowedContentTypes</c> to widen or narrow the set; nothing here is
    /// vertical vocabulary (AGENTS.md).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAllowedContentTypes = new[]
    {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
        "application/pdf",
        "audio/mpeg",
        "audio/ogg",
        "video/mp4",
        "video/webm",
        "text/plain",
    };

    /// <summary>
    /// The default absolute per-object size ceiling when none is configured: 1 GiB. Generous enough for the
    /// media types above while still bounding a single declared object independently of the workspace storage
    /// quota; a deployment raises or lowers it under <c>Assets:Upload:MaxObjectSizeBytes</c>.
    /// </summary>
    public const long DefaultMaxObjectSizeBytes = 1_073_741_824L;

    // Null means "no content-type allowlist restriction" (every well-formed token is admitted); a non-null set
    // is the allowed MIME tokens compared case-insensitively. Production always carries either the curated
    // default list or a configured list (FromConfiguration), so the open mode is reachable only by an explicit
    // empty configuration or the test-only Unrestricted instance.
    private readonly IReadOnlySet<string>? _allowedContentTypes;

    /// <summary>
    /// Creates the upload-acceptance policy. A <see langword="null"/> (or all-blank/empty)
    /// <paramref name="allowedContentTypes"/> means no content-type restriction; a <see langword="null"/>
    /// <paramref name="maxObjectSizeBytes"/> means no absolute size ceiling.
    /// </summary>
    /// <param name="allowedContentTypes">The allowed MIME tokens, or null/empty for no allowlist restriction.</param>
    /// <param name="maxObjectSizeBytes">The absolute per-object byte ceiling, or null for no ceiling.</param>
    /// <exception cref="ArgumentOutOfRangeException">The ceiling is present but not strictly positive.</exception>
    public AssetUploadConstraints(IEnumerable<string>? allowedContentTypes, long? maxObjectSizeBytes)
    {
        if (maxObjectSizeBytes is { } ceiling && ceiling < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxObjectSizeBytes),
                maxObjectSizeBytes,
                "The asset upload size ceiling must be strictly positive.");
        }

        MaxObjectSizeBytes = maxObjectSizeBytes;

        if (allowedContentTypes is null)
        {
            _allowedContentTypes = null;
        }
        else
        {
            var normalized = allowedContentTypes
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // An empty (or all-blank) configured list is treated as "no allowlist restriction" rather than
            // "reject everything", so a deliberately empty configuration opens the gate instead of breaking
            // every upload; a real deployment ships the curated default list.
            _allowedContentTypes = normalized.Count == 0 ? null : normalized;
        }
    }

    /// <summary>The absolute per-object size ceiling in bytes, or <see langword="null"/> when no ceiling applies.</summary>
    public long? MaxObjectSizeBytes { get; }

    /// <summary>Whether a content-type allowlist is enforced (false when every well-formed token is admitted).</summary>
    public bool RestrictsContentType => _allowedContentTypes is not null;

    /// <summary>The allowed MIME tokens (empty when no allowlist restriction is enforced). For diagnostics/tests.</summary>
    public IReadOnlyCollection<string> AllowedContentTypes =>
        _allowedContentTypes is null ? Array.Empty<string>() : _allowedContentTypes.ToArray();

    /// <summary>
    /// A test-only policy that enforces NEITHER an allowlist NOR a ceiling, so a test exercising another aspect
    /// of the upload-intent command is not coupled to the production default acceptance policy. Production never
    /// uses this — <see cref="FromConfiguration"/> always carries the curated defaults or a configured policy.
    /// </summary>
    public static AssetUploadConstraints Unrestricted { get; } =
        new(allowedContentTypes: null, maxObjectSizeBytes: null);

    /// <summary>
    /// Whether the given (already token-validated) content type is admitted by the allowlist. When no allowlist
    /// is enforced every value is allowed; otherwise the match is case-insensitive on the trimmed token. A
    /// blank value is never allowed under an enforced allowlist.
    /// </summary>
    public bool IsContentTypeAllowed(string contentType)
    {
        if (_allowedContentTypes is null)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(contentType)
            && _allowedContentTypes.Contains(contentType.Trim());
    }

    /// <summary>
    /// Whether the given declared object size is within the absolute per-object ceiling. When no ceiling applies
    /// every size is within; otherwise the size must be at most <see cref="MaxObjectSizeBytes"/>.
    /// </summary>
    public bool IsWithinSizeCeiling(long sizeBytes)
        => MaxObjectSizeBytes is not { } ceiling || sizeBytes <= ceiling;

    /// <summary>
    /// Reads the upload-acceptance policy from configuration under <see cref="ConfigurationSection"/>
    /// (<c>Assets:Upload:AllowedContentTypes</c> / <c>Assets:Upload:MaxObjectSizeBytes</c>). An ABSENT allowlist
    /// falls back to the curated <see cref="DefaultAllowedContentTypes"/> (so an unconfigured deployment is
    /// already hardened); an explicitly empty configured list disables the content-type restriction. An absent
    /// ceiling falls back to <see cref="DefaultMaxObjectSizeBytes"/>; a configured ceiling that is not strictly
    /// positive is rejected at startup.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="ArgumentException">The configured size ceiling is not strictly positive.</exception>
    public static AssetUploadConstraints FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);

        // Absent (null) => the curated default allowlist; a present (even empty) list is honoured as configured,
        // so an operator can deliberately widen, narrow or disable the allowlist, but a blank/absent value never
        // silently disables it.
        var configured = section.GetSection("AllowedContentTypes").Get<string[]>();
        var allowedContentTypes = configured ?? DefaultAllowedContentTypes;

        var maxObjectSizeBytes = section.GetValue<long?>("MaxObjectSizeBytes") ?? DefaultMaxObjectSizeBytes;
        if (maxObjectSizeBytes < 1)
        {
            throw new ArgumentException(
                "The configured asset upload size ceiling (Assets:Upload:MaxObjectSizeBytes) must be strictly positive.",
                nameof(configuration));
        }

        return new AssetUploadConstraints(allowedContentTypes, maxObjectSizeBytes);
    }
}
