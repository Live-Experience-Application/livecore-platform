namespace LiveCore.Api.SystemModule;

/// <summary>
/// Idempotency key record of the System module (CORE-VIS-004). This is docs/02_ARCHITECTURE.md's
/// "System" module (csv/database_tables.csv row: table <c>idempotency_keys</c>, module System, scope
/// <c>scope</c>, "Retry safety"); the C# namespace is <c>SystemModule</c> to avoid colliding with the
/// global <c>System</c> namespace.
///
/// An <see cref="IdempotencyKey"/> records that one client request — identified by a generic
/// (<see cref="Scope"/>, <see cref="Key"/>) pair — has been processed, so a retry of the SAME request
/// is recognized and not executed twice (docs/08_API_CONTRACTS.md: "Reveal execution must be
/// idempotent for client retry. A repeated reveal request with the same idempotency key must not
/// create duplicate events."). It is generic infrastructure, owned by the System module and reusable
/// by any write that needs retry safety: the <see cref="Scope"/> is an opaque partition string the
/// calling feature composes (for the reveal command, a per-tenant reveal scope), and the
/// <see cref="Key"/> is the client-supplied <c>Idempotency-Key</c> header value. The documented
/// critical index is <c>idempotency_keys(scope, key)</c> (docs/10_DATABASE_SCHEMA.md), enforced
/// UNIQUE so the same key within a scope can be stored only once — that uniqueness IS the idempotency
/// guarantee.
///
/// Neither field is sensitive content: the scope is a server-composed partition and the key is a
/// client-chosen correlation token, so <see cref="ToString"/> may expose both as identifiers (threat
/// T7 in docs/07_SECURITY_THREAT_MODEL.md concerns free-form CONTENT, not correlation tokens). No
/// response body or payload is stored here — the operation the key guards is itself idempotent at the
/// state level, so a retry re-derives the same result without a cached body.
/// </summary>
public sealed class IdempotencyKey
{
    /// <summary>Maximum accepted length of the scope partition string.</summary>
    public const int MaxScopeLength = 256;

    /// <summary>Maximum accepted length of the client idempotency key.</summary>
    public const int MaxKeyLength = 256;

    private IdempotencyKey(Guid id, string scope, string key, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Idempotency key id must not be empty.", nameof(id));
        }

        if (!IsValidScope(scope))
        {
            throw new ArgumentException("Scope violates the scope invariants.", nameof(scope));
        }

        if (!IsValidKey(key))
        {
            throw new ArgumentException("Key violates the key invariants.", nameof(key));
        }

        Id = id;
        Scope = scope;
        Key = key;
        // Normalized to UTC so the persisted timestamptz value is offset-independent
        // (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private IdempotencyKey()
    {
        Scope = null!;
        Key = null!;
    }

    /// <summary>Surrogate key of the row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md).</summary>
    public Guid Id { get; }

    /// <summary>
    /// Generic partition of the idempotency keyspace, composed by the calling feature (for example a
    /// per-tenant reveal scope). Part of the documented unique key <c>idempotency_keys(scope, key)</c>.
    /// </summary>
    public string Scope { get; }

    /// <summary>
    /// The client-supplied idempotency key (the <c>Idempotency-Key</c> request header). Unique within
    /// its <see cref="Scope"/>.
    /// </summary>
    public string Key { get; }

    /// <summary>When this idempotency key was first recorded (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Records a new idempotency key in the given scope. The caller composes the scope; the key is the
    /// client's <c>Idempotency-Key</c> value.
    /// </summary>
    /// <exception cref="ArgumentException">The scope or the key violates an invariant.</exception>
    public static IdempotencyKey Create(string scope, string key, DateTimeOffset createdAt)
        => new(Guid.CreateVersion7(), scope?.Trim() ?? string.Empty, key?.Trim() ?? string.Empty, createdAt);

    /// <summary>
    /// Whether the given value is a valid scope: non-blank, within the length bound and free of
    /// control characters.
    /// </summary>
    public static bool IsValidScope(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxScopeLength
            && !value.Any(char.IsControl);

    /// <summary>
    /// Whether the given value is a valid key: non-blank, within the length bound and free of control
    /// characters.
    /// </summary>
    public static bool IsValidKey(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxKeyLength
            && !value.Any(char.IsControl);

    /// <summary>
    /// Identifier-only representation safe for structured logs: the row id and the (scope, key)
    /// correlation pair. These are correlation identifiers, not free-form content (threat T7).
    /// </summary>
    public override string ToString()
        => $"IdempotencyKey {Id} scope={Scope} key={Key}";
}
