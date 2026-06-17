// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// Decides when an inbound request may carry its OIDC bearer token in the <c>access_token</c> QUERY
/// STRING instead of the <c>Authorization</c> header (CORE-RT-001, SignalR hub authentication). Browser
/// WebSocket clients cannot set request headers, so the SignalR JavaScript client sends the access token
/// as a query-string parameter on the hub connection; the JWT bearer handler must read it from there for
/// hub connections (the documented pattern for authenticating a SignalR hub).
///
/// SECURITY — query-string tokens are deliberately RESTRICTED to the hub path area. A token in a URL can
/// leak into access logs, proxies and the browser history, so query-string tokens are accepted ONLY for
/// SignalR hub connections (paths under <see cref="PathPrefix"/>) and NEVER for the REST API, which keeps
/// using the <c>Authorization</c> header. <see cref="IsHubPath"/> is the single, unit-tested predicate
/// that enforces this boundary; the JWT bearer <c>OnMessageReceived</c> event consults it before copying
/// a query-string token (threat T7 in docs/07_SECURITY_THREAT_MODEL.md — minimize token exposure).
///
/// The check is string-based (not <c>PathString</c>) so it is trivially unit-testable without an
/// ASP.NET Core framework reference, and matches on a SEGMENT boundary so a look-alike path such as
/// <c>/hubsignup</c> is never treated as the hub area.
/// </summary>
internal static class HubBearerToken
{
    /// <summary>
    /// The path segment under which all SignalR hubs are mapped. A generic transport convention (not a
    /// vertical or feature name); the Realtime module maps its hubs beneath it.
    /// </summary>
    public const string PathPrefix = "/hubs";

    /// <summary>The query-string parameter the SignalR client uses to carry the access token.</summary>
    public const string QueryParameter = "access_token";

    /// <summary>
    /// Whether the given request path is within the SignalR hub area (<see cref="PathPrefix"/> itself or
    /// a sub-path such as <c>/hubs/session</c>), matched case-insensitively and on a segment boundary so
    /// a look-alike like <c>/hubsignup</c> does NOT match. Only such paths may take their bearer token
    /// from the query string.
    /// </summary>
    public static bool IsHubPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (!path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Exactly "/hubs" or a "/hubs/..." sub-path — never "/hubsignup".
        return path.Length == PathPrefix.Length || path[PathPrefix.Length] == '/';
    }
}
