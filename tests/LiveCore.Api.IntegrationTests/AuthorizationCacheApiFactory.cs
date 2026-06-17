// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// A <see cref="WorkspaceApiFactory"/> that observes the production authorization-lookup cache (CORE-PERF-003) over
/// real HTTP. It wires the <see cref="AuthorizationQueryCountingInterceptor"/> into the SQLite test DbContext so a
/// test can assert a repeated request by the same principal does not re-issue the org/profile/membership SELECTs,
/// and pins a long cache TTL so the assertion is never timing-flaky (invalidation, not expiry, is what the tests
/// exercise). The production cache wiring is otherwise unchanged.
/// </summary>
internal sealed class AuthorizationCacheApiFactory : WorkspaceApiFactory
{
    public AuthorizationQueryCountingInterceptor Counter { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // A long TTL so a back-to-back request is always within the window: the tests prove EXPLICIT invalidation
        // on membership change, never reliance on the entry expiring.
        builder.UseSetting($"{LiveCore.Api.Persistence.AuthorizationCacheOptions.ConfigurationSection}:{LiveCore.Api.Persistence.AuthorizationCacheOptions.TtlKey}", "01:00:00");

        base.ConfigureWebHost(builder);
    }

    protected override IInterceptor[] CreateTestDbInterceptors() => [Counter];
}
