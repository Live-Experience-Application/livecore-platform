// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// A test EF Core command interceptor that counts how many SELECTs each request issues against the three
/// authorization-lookup tables the tenant context resolver reads — <c>organizations</c>, <c>users</c> and
/// <c>organization_members</c> — plus <c>workspace_members</c> (CORE-PERF-003). It backs the integration assertion
/// that a repeated request by the same principal does NOT re-issue those lookups within the cache TTL: the test
/// resets the counters, issues the warm request, and asserts each authorization-table count is zero.
///
/// It counts by matching the quoted table name in the SQL the provider generates, so it is robust to the SELECT's
/// shape. The unit tests cover the cache logic itself; this interceptor exists only to make the round-trip count
/// observable end-to-end over real HTTP on the SQLite test provider.
/// </summary>
internal sealed class AuthorizationQueryCountingInterceptor : DbCommandInterceptor
{
    private int _organizations;
    private int _users;
    private int _organizationMembers;
    private int _workspaceMembers;

    public int Organizations => Volatile.Read(ref _organizations);

    public int Users => Volatile.Read(ref _users);

    public int OrganizationMembers => Volatile.Read(ref _organizationMembers);

    public int WorkspaceMembers => Volatile.Read(ref _workspaceMembers);

    public void Reset()
    {
        Interlocked.Exchange(ref _organizations, 0);
        Interlocked.Exchange(ref _users, 0);
        Interlocked.Exchange(ref _organizationMembers, 0);
        Interlocked.Exchange(ref _workspaceMembers, 0);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Count(command.CommandText);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Count(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Count(string sql)
    {
        // The quoted table name appears in the FROM/JOIN clause. "organizations" is not a substring of
        // "organization_members" (the trailing s" disambiguates), so each table is counted independently.
        if (sql.Contains("\"organizations\"", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _organizations);
        }

        if (sql.Contains("\"users\"", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _users);
        }

        if (sql.Contains("\"organization_members\"", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _organizationMembers);
        }

        if (sql.Contains("\"workspace_members\"", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _workspaceMembers);
        }
    }
}
