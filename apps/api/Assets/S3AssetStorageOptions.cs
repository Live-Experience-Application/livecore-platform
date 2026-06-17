using Amazon.Runtime;

namespace LiveCore.Api.Assets;

/// <summary>
/// The deployment's S3-compatible object-storage CONNECTION settings for the concrete signed-URL adapter
/// (CORE-OPS-006, the "Production Operations Readiness" epic) — the endpoint and credentials the
/// <see cref="S3CompatibleAssetStorage"/> uses to sign pre-signed upload/download URLs and to delete objects.
/// This is the credential half of the storage seam, distinct from <see cref="AssetStorageLocation"/> (which
/// records only the private provider/bucket NAMING on each asset row). docs/13_SELF_HOSTING_REQUIREMENTS.md
/// lists "object storage endpoint and credentials" as runtime configuration; they are read here from
/// configuration ONLY (the <c>Assets:Storage:*</c> keys, e.g. the environment variables
/// <c>Assets__Storage__Endpoint</c> / <c>Assets__Storage__AccessKeyId</c> / <c>Assets__Storage__SecretAccessKey</c>),
/// never hardcoded — no storage credential lives in this repository (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// FAIL-CLOSED WHEN UNCONFIGURED. <see cref="IsConfigured"/> is true only when the endpoint AND both
/// credentials are present; a missing or partial configuration is treated as unconfigured, so the
/// registration (<see cref="AssetStorageServiceCollectionExtensions.AddAssetStorage"/>) keeps the
/// fail-closed <see cref="UnconfiguredAssetStorage"/> and asset access DENIES cleanly (503) rather than
/// being served some insecure way — the same private-by-default posture the host holds when it runs without
/// a database connection string or an OIDC authority (the epic acceptance criterion; threat T4 "Asset
/// leak"). The values are the generic S3-compatible connection vocabulary (endpoint, region, path-style,
/// URL lifetime); nothing here carries vertical domain language (AGENTS.md).
///
/// SHORT-LIVED URLS. <see cref="UrlLifetime"/> is the validity window the adapter requests for a signed URL
/// (default 15 minutes). It is validated against the same one-hour ceiling the <see cref="SignedAssetUrl"/>
/// type structurally enforces (<see cref="SignedAssetUrl.MaxLifetime"/>): a configured lifetime that is
/// non-positive or longer than the ceiling is rejected at startup rather than minting a long-lived URL
/// (docs/12_STORAGE_ASSETS.md "signed URLs are short-lived"; "Avoid long-lived signed URLs").
///
/// BOUNDED OUTBOUND CALLS (CORE-RES-005). The one storage operation that makes a real network round-trip —
/// <see cref="S3CompatibleAssetStorage.DeleteObjectAsync(Asset, System.Threading.CancellationToken)"/>, used by
/// the cleanup/retention jobs (minting a pre-signed URL is local, no round-trip) — is bounded so a hung storage
/// backend FAILS FAST instead of holding a worker thread up to the AWS SDK's 100-second default. Three settings
/// configure the SDK client (<see cref="AssetStorageServiceCollectionExtensions"/>): <see cref="RequestTimeout"/>
/// (the per-request <c>Timeout</c>, default 30s), <see cref="MaxErrorRetry"/> (a bounded retry count, default 2)
/// and <see cref="RetryMode"/> (a bounded retry mode, default <see cref="RequestRetryMode.Standard"/>). Defaults
/// are short and safe; a storage failure stays fail-closed and contained (threat T4). These carry no secret
/// (two numbers, a timespan and an enum; threat T7).
/// </summary>
internal sealed class S3AssetStorageOptions
{
    /// <summary>Configuration section the storage connection settings are read from (<c>Assets:Storage</c>).</summary>
    public const string ConfigurationSection = "Assets:Storage";

    /// <summary>
    /// Default signing region when none is configured. Many S3-compatible backends (RustFS/MinIO) accept any
    /// region; <c>us-east-1</c> is the conventional neutral default used in the SigV4 signature.
    /// </summary>
    public const string DefaultRegion = "us-east-1";

    /// <summary>
    /// Default signed-URL lifetime when none is configured: short-lived (15 minutes), comfortably within the
    /// <see cref="SignedAssetUrl.MaxLifetime"/> one-hour ceiling (threat T4; docs/12_STORAGE_ASSETS.md).
    /// </summary>
    public static readonly TimeSpan DefaultUrlLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Default per-request <c>Timeout</c> for the SDK S3 client when none is configured: short (30 seconds), so a
    /// hung storage delete fails fast rather than blocking up to the AWS SDK's 100-second default (CORE-RES-005).
    /// </summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default bounded retry count (<c>MaxErrorRetry</c>) when none is configured: a small, finite number (2), so a
    /// failing storage call is retried a bounded number of times rather than amplifying an outage (CORE-RES-005).
    /// </summary>
    public const int DefaultMaxErrorRetry = 2;

    /// <summary>
    /// Default bounded retry mode (<see cref="RequestRetryMode.Standard"/>) when none is configured. The Standard
    /// mode is the SDK's modern, bounded retry policy (CORE-RES-005).
    /// </summary>
    public const RequestRetryMode DefaultRetryMode = RequestRetryMode.Standard;

    private S3AssetStorageOptions(
        string? endpoint,
        string? accessKeyId,
        string? secretAccessKey,
        string region,
        bool forcePathStyle,
        TimeSpan urlLifetime,
        TimeSpan requestTimeout,
        int maxErrorRetry,
        RequestRetryMode retryMode)
    {
        Endpoint = endpoint;
        AccessKeyId = accessKeyId;
        SecretAccessKey = secretAccessKey;
        Region = region;
        ForcePathStyle = forcePathStyle;
        UrlLifetime = urlLifetime;
        RequestTimeout = requestTimeout;
        MaxErrorRetry = maxErrorRetry;
        RetryMode = retryMode;
    }

    /// <summary>The S3-compatible service endpoint URL (for example a hosted S3 endpoint or a self-hosted RustFS).</summary>
    public string? Endpoint { get; }

    /// <summary>The access key id used to sign requests. Read from configuration only; never hardcoded (threat T7).</summary>
    public string? AccessKeyId { get; }

    /// <summary>The secret access key used to sign requests. Read from configuration only; never hardcoded (threat T7).</summary>
    public string? SecretAccessKey { get; }

    /// <summary>The region used in the SigV4 signature (<see cref="DefaultRegion"/> when unset).</summary>
    public string Region { get; }

    /// <summary>
    /// Whether to address the bucket path-style (<c>endpoint/bucket/key</c>) rather than virtual-hosted-style
    /// (<c>bucket.endpoint/key</c>). Defaults to <see langword="true"/>: path-style is what self-hosted
    /// S3-compatible backends (RustFS/MinIO) require.
    /// </summary>
    public bool ForcePathStyle { get; }

    /// <summary>The validity window requested for a signed URL; short-lived and within <see cref="SignedAssetUrl.MaxLifetime"/>.</summary>
    public TimeSpan UrlLifetime { get; }

    /// <summary>
    /// The per-request <c>Timeout</c> applied to the SDK S3 client, so a hung storage delete fails fast rather than
    /// blocking up to the AWS SDK's 100-second default (CORE-RES-005). Always strictly positive.
    /// </summary>
    public TimeSpan RequestTimeout { get; }

    /// <summary>
    /// The bounded retry count (<c>MaxErrorRetry</c>) applied to the SDK S3 client, so a failing storage call is
    /// retried a bounded number of times rather than amplifying an outage (CORE-RES-005). Never negative.
    /// </summary>
    public int MaxErrorRetry { get; }

    /// <summary>The bounded retry mode (<c>RetryMode</c>) applied to the SDK S3 client (CORE-RES-005).</summary>
    public RequestRetryMode RetryMode { get; }

    /// <summary>
    /// Whether a concrete S3-compatible adapter can be wired: the endpoint AND both credentials are present.
    /// A missing or partial configuration is treated as unconfigured so the registration keeps the
    /// fail-closed <see cref="UnconfiguredAssetStorage"/> default (the epic acceptance criterion; threat T4).
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint)
        && !string.IsNullOrWhiteSpace(AccessKeyId)
        && !string.IsNullOrWhiteSpace(SecretAccessKey);

    /// <summary>
    /// Reads the storage connection settings from configuration under <see cref="ConfigurationSection"/>
    /// (<c>Assets:Storage:Endpoint</c> / <c>:AccessKeyId</c> / <c>:SecretAccessKey</c> / <c>:Region</c> /
    /// <c>:ForcePathStyle</c> / <c>:UrlLifetime</c> / <c>:RequestTimeout</c> / <c>:MaxErrorRetry</c> /
    /// <c>:RetryMode</c>), applying the safe defaults for the optional values. The endpoint, key id and region are
    /// trimmed of surrounding whitespace; the secret is read verbatim. A configured <c>UrlLifetime</c> that is
    /// non-positive or longer than <see cref="SignedAssetUrl.MaxLifetime"/> is rejected so a long-lived URL can
    /// never be requested (threat T4; docs/12_STORAGE_ASSETS.md). A configured <c>RequestTimeout</c> that is
    /// non-positive, a negative <c>MaxErrorRetry</c> or an unrecognised <c>RetryMode</c> is rejected at startup so a
    /// misconfiguration can never silently remove the outbound-call bound (CORE-RES-005).
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="ArgumentException">A configured URL lifetime, request timeout, retry count or retry mode is invalid.</exception>
    public static S3AssetStorageOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);
        var endpoint = section["Endpoint"];
        var accessKeyId = section["AccessKeyId"];
        var secretAccessKey = section["SecretAccessKey"];
        var region = section["Region"];
        var forcePathStyle = section.GetValue("ForcePathStyle", true);
        var urlLifetime = section.GetValue<TimeSpan?>("UrlLifetime") ?? DefaultUrlLifetime;
        var requestTimeout = section.GetValue<TimeSpan?>("RequestTimeout") ?? DefaultRequestTimeout;
        var maxErrorRetry = section.GetValue("MaxErrorRetry", DefaultMaxErrorRetry);
        var retryMode = ParseRetryMode(section["RetryMode"]);

        // A signed URL must be short-lived: reject a non-positive or over-ceiling lifetime at startup rather
        // than letting the adapter request a long-lived URL the SignedAssetUrl type would later refuse.
        if (urlLifetime <= TimeSpan.Zero || urlLifetime > SignedAssetUrl.MaxLifetime)
        {
            throw new ArgumentException(
                $"The configured asset storage URL lifetime must be strictly positive and at most "
                + $"{SignedAssetUrl.MaxLifetime}.",
                nameof(configuration));
        }

        // The outbound-call bound must stay a real ceiling: a non-positive Timeout would let the SDK fall back to
        // its 100-second default (defeating fail-fast), and a negative retry count is meaningless. Reject both at
        // startup rather than silently degrading the bound (CORE-RES-005).
        if (requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The configured asset storage request timeout must be strictly positive.",
                nameof(configuration));
        }

        if (maxErrorRetry < 0)
        {
            throw new ArgumentException(
                "The configured asset storage max error retry count cannot be negative.",
                nameof(configuration));
        }

        return new S3AssetStorageOptions(
            string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim(),
            string.IsNullOrWhiteSpace(accessKeyId) ? null : accessKeyId.Trim(),
            string.IsNullOrWhiteSpace(secretAccessKey) ? null : secretAccessKey,
            string.IsNullOrWhiteSpace(region) ? DefaultRegion : region.Trim(),
            forcePathStyle,
            urlLifetime,
            requestTimeout,
            maxErrorRetry,
            retryMode);
    }

    /// <summary>
    /// Parses the configured <c>RetryMode</c> into a <see cref="RequestRetryMode"/>, falling back to the safe
    /// default when blank. A present but unrecognised value is rejected at startup rather than silently ignored.
    /// </summary>
    private static RequestRetryMode ParseRetryMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultRetryMode;
        }

        if (!Enum.TryParse<RequestRetryMode>(value.Trim(), ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            throw new ArgumentException(
                $"The configured asset storage retry mode '{value}' is not a recognised retry mode "
                + $"({string.Join(", ", Enum.GetNames<RequestRetryMode>())}).",
                nameof(value));
        }

        return parsed;
    }
}
