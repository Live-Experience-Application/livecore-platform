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

    private S3AssetStorageOptions(
        string? endpoint,
        string? accessKeyId,
        string? secretAccessKey,
        string region,
        bool forcePathStyle,
        TimeSpan urlLifetime)
    {
        Endpoint = endpoint;
        AccessKeyId = accessKeyId;
        SecretAccessKey = secretAccessKey;
        Region = region;
        ForcePathStyle = forcePathStyle;
        UrlLifetime = urlLifetime;
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
    /// <c>:ForcePathStyle</c> / <c>:UrlLifetime</c>), applying the safe defaults for the optional values. The
    /// endpoint, key id and region are trimmed of surrounding whitespace; the secret is read verbatim. A
    /// configured <c>UrlLifetime</c> that is non-positive or longer than <see cref="SignedAssetUrl.MaxLifetime"/>
    /// is rejected so a long-lived URL can never be requested (threat T4; docs/12_STORAGE_ASSETS.md).
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="ArgumentException">A configured URL lifetime is non-positive or exceeds the ceiling.</exception>
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

        // A signed URL must be short-lived: reject a non-positive or over-ceiling lifetime at startup rather
        // than letting the adapter request a long-lived URL the SignedAssetUrl type would later refuse.
        if (urlLifetime <= TimeSpan.Zero || urlLifetime > SignedAssetUrl.MaxLifetime)
        {
            throw new ArgumentException(
                $"The configured asset storage URL lifetime must be strictly positive and at most "
                + $"{SignedAssetUrl.MaxLifetime}.",
                nameof(configuration));
        }

        return new S3AssetStorageOptions(
            string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim(),
            string.IsNullOrWhiteSpace(accessKeyId) ? null : accessKeyId.Trim(),
            string.IsNullOrWhiteSpace(secretAccessKey) ? null : secretAccessKey,
            string.IsNullOrWhiteSpace(region) ? DefaultRegion : region.Trim(),
            forcePathStyle,
            urlLifetime);
    }
}
