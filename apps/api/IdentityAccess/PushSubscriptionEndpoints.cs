// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// HTTP endpoints of the IdentityAccess module's closed-app Web Push subscription surface (CORE-PUSH-001, the
/// "Closed-App Push Notifications" epic, raised by a vertical adopter — ARC-GAP-005, the enabler). They let an
/// authenticated principal register and remove its OWN browser push subscription with Core and read the
/// deployment VAPID public key it must subscribe against, so a vertical adopter need not duplicate Core
/// authorization. Closed-app DELIVERY (the outbound, signed, content-free push) is a later story, CORE-PUSH-002.
///
/// Routes owned by this story (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>GET /api/v1/push/vapid-public-key</c> — the deployment VAPID public key a client subscribes
///   against, or <see langword="null"/> when the surface is inert.</item>
///   <item><c>POST /api/v1/me/push-subscriptions</c> — register (idempotently) the caller's push subscription.</item>
///   <item><c>DELETE /api/v1/me/push-subscriptions/{subscriptionId}</c> — remove the caller's push subscription.</item>
/// </list>
///
/// Authorization model (object-level, server-side, fail-closed; docs/06_AUTHORIZATION_MATRIX.md; threats
/// T1/T5/T7):
/// <list type="bullet">
///   <item>The route group requires authorization, so an anonymous caller is challenged with 401 before any
///   handler runs; a failed principal mapping is 401 and its reason is never echoed (threat T7).</item>
///   <item>A push subscription is a USER concept (only a human user has a browser): a service-account
///   principal is denied 403 on the <c>/me</c> register/delete routes, exactly as the sibling <c>/me</c>
///   routes deny a service account.</item>
///   <item>The subscription is scoped ENTIRELY to the caller's server-resolved user-profile id, never a
///   client-supplied principal id, so a caller can ONLY ever register, refresh or delete its OWN subscription.
///   A delete addressing another principal's subscription id (or an unknown id) removes nothing and is an
///   indistinguishable hidden 404 — a caller can never delete (or even probe) another principal's
///   subscription (threats T1/T5).</item>
///   <item>INERT WHEN UNCONFIGURED: with no VAPID key configured (<see cref="WebPushOptions.IsConfigured"/>
///   false) the public-key route returns a null key and registration is refused 503 — no subscription is
///   registrable (the story's acceptance criterion). Deletion stays available so a stale subscription can
///   always be cleaned up.</item>
/// </list>
///
/// Persistence dependency: like the sibling <c>/me</c> routes, the register/delete handlers use the
/// user-profile reference service and the push-subscription repository, registered only when a database
/// connection string is configured (see <c>Program.cs</c>); when persistence is off they fail closed with 503.
/// The public-key route needs no persistence (it reads only deployment configuration).
/// </summary>
internal static class PushSubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapPushSubscriptionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token is
        // challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1")
            .RequireAuthorization();

        group.MapGet("/push/vapid-public-key", GetVapidPublicKey);
        group.MapPost("/me/push-subscriptions", RegisterPushSubscriptionAsync);
        group.MapDelete("/me/push-subscriptions/{subscriptionId:guid}", DeletePushSubscriptionAsync);

        return endpoints;
    }

    // GET /api/v1/push/vapid-public-key
    private static IResult GetVapidPublicKey(HttpContext httpContext)
    {
        // The VAPID public key is published deployment configuration a client needs to subscribe; any
        // authenticated caller may read it (no persistence, no principal-type restriction). When no key is
        // configured the surface is inert: the response carries a null key, never an error.
        var webPush = httpContext.RequestServices.GetRequiredService<WebPushOptions>();
        return Results.Ok(new PushVapidPublicKeyResponse(webPush.PublicKey));
    }

    // POST /api/v1/me/push-subscriptions
    private static async Task<IResult> RegisterPushSubscriptionAsync(
        HttpContext httpContext,
        [FromBody] RegisterPushSubscriptionRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetDependencies(httpContext, out var deps))
        {
            return ServiceUnavailable("Push subscription registration requires persistence, which is not configured.");
        }

        if (!TryMapPrincipal(httpContext, out var principal))
        {
            return Unauthorized();
        }

        // A push subscription is a user concept: a service account has no browser, so it is denied fail-closed
        // (403), exactly as the sibling /me routes deny a service account.
        if (principal.Type != PrincipalType.User)
        {
            return Forbidden();
        }

        // INERT WHEN UNCONFIGURED (the story's acceptance criterion): with no VAPID key configured no
        // subscription is registrable, so registration is refused 503 — the same private-by-default posture the
        // host holds without object storage or an OIDC authority.
        var webPush = httpContext.RequestServices.GetRequiredService<WebPushOptions>();
        if (!webPush.IsConfigured)
        {
            return ServiceUnavailable("Web Push is not configured; no subscription is registrable.");
        }

        // Validate the body up front for every authenticated user (a broken body is a 400 regardless of who is
        // calling). There is no resource existence to hide on a /me route, so validating the request shape here
        // leaks nothing (threat T7).
        if (request is null)
        {
            return ValidationError("A request body is required.");
        }

        var endpoint = request.Endpoint?.Trim();
        if (!PushSubscription.IsValidEndpoint(endpoint))
        {
            return ValidationError("A valid absolute https push endpoint is required.");
        }

        if (!PushSubscription.IsValidKey(request.P256dh))
        {
            return ValidationError("A valid p256dh key is required.");
        }

        if (!PushSubscription.IsValidKey(request.Auth))
        {
            return ValidationError("A valid auth key is required.");
        }

        // Resolve the caller's profile (the canonical "current user" resolution; idempotent on first sight),
        // then register the subscription scoped to that profile id — never a client-supplied principal id.
        var profile = await deps.UserProfiles
            .EnsureUserProfileAsync(principal, cancellationToken)
            .ConfigureAwait(false);

        var subscription = await deps.Registration
            .RegisterAsync(profile.Id, endpoint!, request.P256dh!, request.Auth!, cancellationToken)
            .ConfigureAwait(false);

        var response = PushSubscriptionResponse.From(subscription);
        return Results.Created($"/api/v1/me/push-subscriptions/{subscription.Id}", response);
    }

    // DELETE /api/v1/me/push-subscriptions/{subscriptionId}
    private static async Task<IResult> DeletePushSubscriptionAsync(
        HttpContext httpContext,
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        if (!TryGetDependencies(httpContext, out var deps))
        {
            return ServiceUnavailable("Push subscription deletion requires persistence, which is not configured.");
        }

        if (!TryMapPrincipal(httpContext, out var principal))
        {
            return Unauthorized();
        }

        if (principal.Type != PrincipalType.User)
        {
            return Forbidden();
        }

        // Resolve the caller's profile, then delete SCOPED to that profile id. The delete predicate leads with
        // the owning principal, so a caller can never delete another principal's subscription: a foreign or
        // unknown id removes nothing and is an indistinguishable hidden 404 (fail-closed; threats T1/T5).
        var profile = await deps.UserProfiles
            .EnsureUserProfileAsync(principal, cancellationToken)
            .ConfigureAwait(false);

        var deleted = await deps.Subscriptions
            .DeleteByIdForUserAsync(subscriptionId, profile.Id, cancellationToken)
            .ConfigureAwait(false);

        return deleted ? Results.NoContent() : HiddenSubscription();
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a database
    /// connection string is configured; when absent, the register/delete endpoints fail closed with 503 instead
    /// of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out PushSubscriptionEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var userProfiles = services.GetService<UserProfileReferenceService>();
        var registration = services.GetService<PushSubscriptionRegistrationService>();
        var subscriptions = services.GetService<IPushSubscriptionRepository>();

        if (userProfiles is null || registration is null || subscriptions is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new PushSubscriptionEndpointDependencies(userProfiles, registration, subscriptions);
        return true;
    }

    /// <summary>
    /// Maps the authenticated principal to an <see cref="OidcPrincipal"/>, fail-closed: a failed mapping yields
    /// <see langword="false"/> (the caller returns 401). The mapping error reason is never echoed (threat T7).
    /// </summary>
    private static bool TryMapPrincipal(HttpContext httpContext, out OidcPrincipal principal)
    {
        var result = OidcPrincipalMapper.Map(httpContext.User);
        if (!result.Succeeded)
        {
            principal = null!;
            return false;
        }

        principal = result.Principal;
        return true;
    }

    private static IResult ServiceUnavailable(string detail)
        => CoreProblem.Create(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            code: ProblemCodes.ServiceUnavailable,
            title: "Service Unavailable",
            detail: detail);

    private static IResult Unauthorized()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status401Unauthorized,
            code: ProblemCodes.AuthenticationRequired,
            title: "Unauthorized",
            detail: "Valid authentication is required.");

    private static IResult Forbidden()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status403Forbidden,
            code: ProblemCodes.PermissionDenied,
            title: "Forbidden",
            detail: "You are not authorized to perform this action.");

    private static IResult ValidationError(string detail)
        => CoreProblem.Create(
            statusCode: StatusCodes.Status400BadRequest,
            code: ProblemCodes.ValidationError,
            title: "Bad Request",
            detail: detail);

    private static IResult HiddenSubscription()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status404NotFound,
            code: ProblemCodes.NotFound,
            title: "Not Found",
            detail: "The push subscription was not found.");

    private readonly record struct PushSubscriptionEndpointDependencies(
        UserProfileReferenceService UserProfiles,
        PushSubscriptionRegistrationService Registration,
        IPushSubscriptionRepository Subscriptions);
}
