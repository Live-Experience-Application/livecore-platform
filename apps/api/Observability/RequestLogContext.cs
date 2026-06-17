// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Collections;

namespace LiveCore.Api.Observability;

/// <summary>
/// Per-request structured log context (CORE-OBS-002).
///
/// docs/15_OBSERVABILITY.md requires that every request/event log line carry a documented set of
/// identifiers — <c>request_id</c>, <c>organization_id</c>, <c>workspace_id</c>, <c>session_id</c> (when
/// applicable), <c>user_id</c> (when applicable) and <c>event_id</c> (when applicable). JSON logging is
/// already wired with <c>IncludeScopes</c> (CORE-FND-004), but nothing populated those keys. This is the
/// single, request-scoped holder of them: <see cref="RequestLogContextMiddleware"/> opens one logging scope
/// around the request with this object as the scope state, and the AUTHORITATIVE resolution points enrich it
/// as the identifiers become known (the middleware seeds <c>request_id</c> and <c>user_id</c>; the
/// Organizations tenant context resolver sets <c>organization_id</c> from the resolved tenant; the Realtime
/// session event publisher sets <c>event_id</c> from the published event). Because the object is mutable and
/// the JSON console formatter enumerates the scope state when it writes EACH entry, every log line emitted
/// after a key is set carries that key, so a single request's log lines converge on the full documented
/// context.
///
/// THREAT T7 (log leaks, docs/07_SECURITY_THREAT_MODEL.md). Only identifiers and authorization metadata are
/// ever stored here — opaque surrogate ids and the principal's issuer-local subject — never the access token,
/// the display name, the email or any resource content. The keys are exactly the documented snake_case names
/// so a log consumer can correlate by them. Enriching is best-effort and never throws: a logging concern must
/// never break a request, so a malformed or empty value is ignored rather than rejected.
/// </summary>
public sealed class RequestLogContext : IReadOnlyCollection<KeyValuePair<string, object>>
{
    /// <summary>Documented log key for the per-request correlation id.</summary>
    public const string RequestIdKey = "request_id";

    /// <summary>Documented log key for the resolved tenant organization's surrogate id.</summary>
    public const string OrganizationIdKey = "organization_id";

    /// <summary>Documented log key for the addressed workspace's surrogate id.</summary>
    public const string WorkspaceIdKey = "workspace_id";

    /// <summary>Documented log key for the addressed session's surrogate id.</summary>
    public const string SessionIdKey = "session_id";

    /// <summary>Documented log key for the authenticated principal's issuer-local subject.</summary>
    public const string UserIdKey = "user_id";

    /// <summary>Documented log key for the published session event's id.</summary>
    public const string EventIdKey = "event_id";

    private string? _requestId;
    private string? _organizationId;
    private string? _workspaceId;
    private string? _sessionId;
    private string? _userId;
    private string? _eventId;

    /// <summary>
    /// Sets the per-request correlation id (the value ASP.NET Core assigns to the request). Ignored when
    /// blank so a logging concern never throws.
    /// </summary>
    public void SetRequestId(string? requestId)
    {
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            _requestId = requestId;
        }
    }

    /// <summary>
    /// Sets the authenticated principal's user identity (the OIDC issuer-local subject). The subject is an
    /// opaque, log-safe identifier (see <c>OidcPrincipal.ToString</c>), never the display name or email
    /// (threat T7). Ignored when blank.
    /// </summary>
    public void SetUserId(string? subjectId)
    {
        if (!string.IsNullOrWhiteSpace(subjectId))
        {
            _userId = subjectId;
        }
    }

    /// <summary>Sets the resolved tenant organization id. The empty id is ignored.</summary>
    public void SetOrganizationId(Guid organizationId)
    {
        if (organizationId != Guid.Empty)
        {
            _organizationId = Format(organizationId);
        }
    }

    /// <summary>Sets the addressed workspace id. The empty id is ignored.</summary>
    public void SetWorkspaceId(Guid workspaceId)
    {
        if (workspaceId != Guid.Empty)
        {
            _workspaceId = Format(workspaceId);
        }
    }

    /// <summary>Sets the addressed session id. The empty id is ignored.</summary>
    public void SetSessionId(Guid sessionId)
    {
        if (sessionId != Guid.Empty)
        {
            _sessionId = Format(sessionId);
        }
    }

    /// <summary>Sets the published session event id. The empty id is ignored.</summary>
    public void SetEventId(Guid eventId)
    {
        if (eventId != Guid.Empty)
        {
            _eventId = Format(eventId);
        }
    }

    /// <summary>The number of documented keys currently populated.</summary>
    public int Count
    {
        get
        {
            var count = 0;
            if (_requestId is not null)
            {
                count++;
            }

            if (_organizationId is not null)
            {
                count++;
            }

            if (_workspaceId is not null)
            {
                count++;
            }

            if (_sessionId is not null)
            {
                count++;
            }

            if (_userId is not null)
            {
                count++;
            }

            if (_eventId is not null)
            {
                count++;
            }

            return count;
        }
    }

    /// <summary>
    /// Enumerates only the populated keys, with their current values, so the JSON console formatter renders
    /// each set identifier as a named scope property and omits the unset ones. Enumerated at log-write time,
    /// so a key set after the scope opened still appears on later log lines.
    /// </summary>
    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        if (_requestId is not null)
        {
            yield return new KeyValuePair<string, object>(RequestIdKey, _requestId);
        }

        if (_organizationId is not null)
        {
            yield return new KeyValuePair<string, object>(OrganizationIdKey, _organizationId);
        }

        if (_workspaceId is not null)
        {
            yield return new KeyValuePair<string, object>(WorkspaceIdKey, _workspaceId);
        }

        if (_sessionId is not null)
        {
            yield return new KeyValuePair<string, object>(SessionIdKey, _sessionId);
        }

        if (_userId is not null)
        {
            yield return new KeyValuePair<string, object>(UserIdKey, _userId);
        }

        if (_eventId is not null)
        {
            yield return new KeyValuePair<string, object>(EventIdKey, _eventId);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// A compact, log-safe <c>key=value</c> rendering of the populated identifiers (the JSON console
    /// formatter writes it as the scope's <c>Message</c>). Carries only identifiers, never content (threat T7).
    /// </summary>
    public override string ToString() => string.Join(' ', this.Select(pair => $"{pair.Key}={pair.Value}"));

    private static string Format(Guid id) => id.ToString("D");
}
