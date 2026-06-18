// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Host tests for the log-level operator configuration surface (CORE-OBS-011). The story's required tests:
/// <list type="bullet">
///   <item>overriding <c>Logging__LogLevel__Default</c> via configuration (the same key the environment
///   variable maps to) changes the host's MINIMUM EMITTED level — a <c>Debug</c> line the host suppresses at
///   the shipped <c>Information</c> default is emitted once the default is lowered to <c>Debug</c>;</item>
///   <item>a doc review that every logging env key documented in the names-only <c>.env.example</c> contract
///   actually RESOLVES in the running host (and that the only documented logging surface is the LEVEL — the
///   JSON console FORMAT is a fixed, safe default, not an operator knob).</item>
/// </list>
///
/// They boot the real <see cref="Program"/> host (no database/identity needed — logging is wired
/// unconditionally at the top of the host) and add a <see cref="CapturingLoggerProvider"/> alongside the
/// production JSON console formatter, so the level filter observed is exactly the one a real log sink obeys.
/// <c>ClearProviders()</c>/<c>AddJsonConsole</c> in the host replace only the PROVIDERS and leave the
/// configuration-driven log filtering intact, which is what makes the standard .NET keys take effect.
/// </summary>
public sealed class LoggingLevelConfigurationTests
{
    private const string _probeCategory = "LiveCore.LoggingLevelConfigurationTests.Probe";
    private const string _loggingKeyPrefix = "Logging__LogLevel__";

    [Fact]
    public void Shipped_information_default_suppresses_debug_emission()
    {
        // No override: the host uses the shipped safe default (appsettings.json Logging:LogLevel:Default =
        // Information), so a Debug line is below the minimum emitted level and never reaches a sink.
        using var factory = CreateHost(new Dictionary<string, string?>(), out var logs);
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(_probeCategory);

        Assert.False(logger.IsEnabled(LogLevel.Debug));

        logger.LogDebug("logging-probe-debug-line");
        logger.LogInformation("logging-probe-information-line");

        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("logging-probe-debug-line"));
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("logging-probe-information-line"));
    }

    [Fact]
    public void Overriding_default_to_debug_raises_the_minimum_emitted_level()
    {
        // Logging:LogLevel:Default is the key the environment variable Logging__LogLevel__Default maps to
        // (".NET reads A:B:C from A__B__C"). Lowering it to Debug demonstrably raises emitted verbosity: the
        // SAME Debug line the previous test suppressed is now emitted.
        using var factory = CreateHost(
            new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Debug" },
            out var logs);
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(_probeCategory);

        Assert.True(logger.IsEnabled(LogLevel.Debug));

        logger.LogDebug("logging-probe-debug-line");

        Assert.Contains(logs.Entries, entry => entry.Message.Contains("logging-probe-debug-line"));
    }

    [Fact]
    public void Every_documented_logging_env_key_resolves_in_the_host()
    {
        var documented = ReadDocumentedLoggingEnvKeys();

        // The contract documents at least the default minimum level.
        Assert.Contains("Logging__LogLevel__Default", documented);

        // The host exposes the LEVEL as the only logging configuration surface (the JSON FORMAT is fixed,
        // CORE-OBS-011). A documented logging key of any other shape — for example a console-formatter key —
        // would be one the host does not honor, so the contract must list only Logging:LogLevel:<Category> keys.
        foreach (var envKey in documented)
        {
            Assert.StartsWith(_loggingKeyPrefix, envKey, StringComparison.Ordinal);
        }

        // Behavioral proof that each documented key actually RESOLVES in the running host. Raise the Default
        // minimum to Error and lower every per-category key to Trace in one host, so each documented key
        // visibly changes the effective level of the category it governs (a key the host ignored could not).
        var overrides = new Dictionary<string, string?>();
        foreach (var envKey in documented)
        {
            var configKey = envKey.Replace("__", ":");
            overrides[configKey] = IsDefaultKey(envKey) ? "Error" : "Trace";
        }

        using var factory = CreateHost(overrides, out _);
        var loggerFactory = factory.Services.GetRequiredService<ILoggerFactory>();

        foreach (var envKey in documented)
        {
            if (IsDefaultKey(envKey))
            {
                // Default governs every otherwise-unmatched category. Raised to Error, an unmatched category
                // emits Error but no longer Information — proving the Default key resolved, since its shipped
                // appsettings value (Information) would still enable Information.
                var unmatched = loggerFactory.CreateLogger("LiveCore.LoggingLevelDocReview.UnmatchedCategory");
                Assert.True(
                    unmatched.IsEnabled(LogLevel.Error),
                    $"Documented logging key {envKey} did not resolve in the host.");
                Assert.False(
                    unmatched.IsEnabled(LogLevel.Information),
                    $"Documented logging key {envKey} did not resolve in the host.");
            }
            else
            {
                // A per-category override lowered to Trace enables Trace for its category prefix even though
                // Default is Error — only an honored override could do that.
                var category = envKey[_loggingKeyPrefix.Length..];
                var logger = loggerFactory.CreateLogger(category);
                Assert.True(
                    logger.IsEnabled(LogLevel.Trace),
                    $"Documented logging key {envKey} did not resolve in the host.");
            }
        }
    }

    private static bool IsDefaultKey(string envKey) =>
        string.Equals(envKey, "Logging__LogLevel__Default", StringComparison.Ordinal);

    private static WebApplicationFactory<Program> CreateHost(
        IReadOnlyDictionary<string, string?> overrides,
        out CapturingLoggerProvider logs)
    {
        var captured = new CapturingLoggerProvider();
        logs = captured;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(overrides));
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(captured));
        });
    }

    private static IReadOnlyList<string> ReadDocumentedLoggingEnvKeys()
    {
        var envExample = Path.Combine(RepositoryRoot(), ".env.example");
        var keys = new List<string>();
        foreach (var raw in File.ReadAllLines(envExample))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            if (name.StartsWith("Logging__", StringComparison.Ordinal))
            {
                keys.Add(name);
            }
        }

        return keys;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".env.example")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (.env.example) upward from {AppContext.BaseDirectory}.");
    }
}
