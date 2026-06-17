// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Test-only logging provider that captures every emitted log entry together with the structured values of
/// the active logging scopes (CORE-OBS-002 integration tests). It implements <see cref="ISupportExternalScope"/>
/// so the logging factory hands it the SAME external scope provider every other provider receives (including
/// the production JSON console formatter), so the scopes it observes are exactly the scopes a real log sink
/// renders. It exists only in the test project and is registered only by the test factory; production logging
/// is unchanged.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();
    private IExternalScopeProvider? _scopeProvider;

    /// <summary>A snapshot of the captured entries, oldest first.</summary>
    public IReadOnlyList<CapturedLogEntry> Entries => _entries.ToArray();

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

    public void Dispose()
    {
    }

    private void Capture(string category, LogLevel level, string message)
    {
        // Merge the structured values of every active scope, exactly as the JSON console formatter would.
        var scopeValues = new Dictionary<string, object?>(StringComparer.Ordinal);
        _scopeProvider?.ForEachScope(
            static (scope, state) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
                {
                    foreach (var pair in pairs)
                    {
                        state[pair.Key] = pair.Value;
                    }
                }
            },
            scopeValues);

        _entries.Enqueue(new CapturedLogEntry(category, level, message, scopeValues));
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly CapturingLoggerProvider _provider;

        public CapturingLogger(string category, CapturingLoggerProvider provider)
        {
            _category = category;
            _provider = provider;
        }

        // Scopes are tracked by the shared external scope provider, not per provider-logger, so this is a no-op.
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
            _provider.Capture(_category, logLevel, formatter(state, exception));
        }
    }
}

/// <summary>A captured log entry: its category, level, formatted message and the merged scope values.</summary>
internal sealed record CapturedLogEntry(
    string Category,
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> ScopeValues);
