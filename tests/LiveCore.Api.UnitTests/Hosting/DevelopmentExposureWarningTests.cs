// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Tests for the Development-default exposure warning (CORE-OPS-011). The bundled stack defaults to the
/// Development environment so it comes up green with no identity provider — but in Development the production
/// readiness gate is inert (CORE-OPS-005) and the OIDC audience guard does not trip (CORE-OPS-004), so a host
/// reachable beyond localhost in that posture is green but unauthenticated. These pin the pure decision (only
/// Development AND a non-loopback bind warns) and the host logging path (the warning is actually emitted), while
/// proving the legitimate local-development default — Development on loopback — is NOT weakened.
/// </summary>
public class DevelopmentExposureWarningTests
{
    [Theory]
    // Loopback binds are never an exposure (the legitimate local-development default).
    [InlineData("http://localhost:5000", false)]
    [InlineData("http://127.0.0.1:5000", false)]
    [InlineData("http://127.5.5.5:5000", false)] // all of 127.0.0.0/8 is loopback
    [InlineData("http://[::1]:5000", false)]
    [InlineData("https://LOCALHOST:5001", false)] // case-insensitive
    // Wildcard / any-interface binds and concrete non-loopback hosts ARE reachable off-box.
    [InlineData("http://0.0.0.0:8080", true)]
    [InlineData("http://[::]:8080", true)]
    [InlineData("http://*:8080", true)]
    [InlineData("http://+:8080", true)]
    [InlineData("http://10.0.0.5:8080", true)]
    [InlineData("http://app.example.com:8080", true)]
    // Nothing bound is no exposure.
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsNonLoopbackBindAddress_classifies_each_bind(string? address, bool expected)
    {
        Assert.Equal(expected, DevelopmentExposureWarning.IsNonLoopbackBindAddress(address));
    }

    [Fact]
    public void IsExposedDevelopmentBind_is_true_for_development_on_a_non_loopback_bind()
    {
        Assert.True(DevelopmentExposureWarning.IsExposedDevelopmentBind(
            isDevelopmentEnvironment: true, ["http://0.0.0.0:8080"]));
    }

    [Fact]
    public void IsExposedDevelopmentBind_is_true_when_any_bind_is_non_loopback()
    {
        // A host can bind several addresses; one exposed interface is enough.
        Assert.True(DevelopmentExposureWarning.IsExposedDevelopmentBind(
            isDevelopmentEnvironment: true, ["http://localhost:5000", "http://0.0.0.0:8080"]));
    }

    [Fact]
    public void IsExposedDevelopmentBind_is_false_for_development_only_on_loopback()
    {
        // The legitimate local-development default (Development bound to loopback) is not weakened.
        Assert.False(DevelopmentExposureWarning.IsExposedDevelopmentBind(
            isDevelopmentEnvironment: true, ["http://localhost:5000", "http://[::1]:5000"]));
    }

    [Fact]
    public void IsExposedDevelopmentBind_is_false_outside_development_even_on_a_non_loopback_bind()
    {
        // Production is the safe posture (and is separately guarded by CORE-OPS-004/005), so it never warns.
        Assert.False(DevelopmentExposureWarning.IsExposedDevelopmentBind(
            isDevelopmentEnvironment: false, ["http://0.0.0.0:8080"]));
    }

    [Fact]
    public void IsExposedDevelopmentBind_is_false_when_nothing_is_bound()
    {
        Assert.False(DevelopmentExposureWarning.IsExposedDevelopmentBind(
            isDevelopmentEnvironment: true, []));
    }

    [Fact]
    public void WarnIfExposedDevelopmentBind_logs_a_warning_for_development_on_a_non_loopback_bind()
    {
        // The host test: a Development host bound to a non-loopback interface (the green-but-unauthenticated
        // footgun) emits the loud startup warning, naming the bound address so the operator can see what is
        // exposed.
        var logger = new CapturingLogger();

        DevelopmentExposureWarning.WarnIfExposedDevelopmentBind(
            logger, isDevelopmentEnvironment: true, ["http://0.0.0.0:8080"]);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Development", entry.Message, StringComparison.Ordinal);
        Assert.Contains("http://0.0.0.0:8080", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WarnIfExposedDevelopmentBind_is_silent_for_development_on_loopback()
    {
        // No warning for the normal local-development posture.
        var logger = new CapturingLogger();

        DevelopmentExposureWarning.WarnIfExposedDevelopmentBind(
            logger, isDevelopmentEnvironment: true, ["http://localhost:5000"]);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void WarnIfExposedDevelopmentBind_is_silent_outside_development()
    {
        // The production posture never warns (it is the safe one).
        var logger = new CapturingLogger();

        DevelopmentExposureWarning.WarnIfExposedDevelopmentBind(
            logger, isDevelopmentEnvironment: false, ["http://0.0.0.0:8080"]);

        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// Minimal <see cref="ILogger"/> that records the level and rendered message of each log entry, so the
    /// host warning's actual logging path is exercised (not just the pure decision).
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
