using System.Reflection;
using System.Text.Json;

using LiveCore.Api.Realtime;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Tests for <see cref="SessionEventPayloads"/> (CORE-RT-008) — the single C# source of the identifier-only
/// session-event payload contracts. They pin two invariants the published TypeScript contract
/// (<c>@livecore/contracts</c>) and its drift gate depend on:
/// <list type="number">
///   <item>EVERY emitted event has EXACTLY ONE payload contract: <see cref="SessionEventPayloads.ByEventType"/>
///   keys equal the <see cref="SessionEventTypes"/> constants in both directions, so the mapping cannot list a
///   payload for an event no command emits, nor leave an emitted event without a payload (the payload analogue
///   of spec-consistency check 11, CORE-EVT-004);</item>
///   <item>each payload record serializes — with the SAME default <c>System.Text.Json</c> options the command
///   sites use — to EXACTLY its CLR (PascalCase) property names, all of which are resource IDENTIFIERS or
///   generic state names (Guid/string) only, never nested content (threat T7). This is the wire shape the
///   TypeScript <c>KnownSessionEventPayloadFields</c> mirror and the drift gate enforces.</item>
/// </list>
/// All fixtures are generic Core vocabulary (AGENTS.md).
/// </summary>
public sealed class SessionEventPayloadsTests
{
    /// <summary>The emitted event-type names: the <c>public const string</c> values of the catalog.</summary>
    private static IReadOnlySet<string> EmittedEventTypes()
        => typeof(SessionEventTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void ByEventType_maps_exactly_the_emitted_event_vocabulary()
    {
        var emitted = EmittedEventTypes();
        var mapped = SessionEventPayloads.ByEventType.Keys.ToHashSet(StringComparer.Ordinal);

        // Both directions: no emitted event lacks a payload contract, and no payload contract is mapped to an
        // event the catalog does not emit. This is the C#-side binding the TypeScript drift gate mirrors.
        Assert.Empty(emitted.Except(mapped));
        Assert.Empty(mapped.Except(emitted));

        // The ten non-deferred catalog events (CORE-EVT-004); a missing/extra constant trips the binding above.
        Assert.Equal(10, mapped.Count);
    }

    [Fact]
    public void Every_payload_record_serializes_to_its_identifier_only_pascal_case_fields()
    {
        foreach (var (eventType, payloadType) in SessionEventPayloads.ByEventType)
        {
            var properties = payloadType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            Assert.NotEmpty(properties);

            // IDENTIFIER-ONLY (threat T7): every field is a Guid id or a generic state name (string) — never a
            // nested object, collection or content body.
            foreach (var property in properties)
            {
                Assert.True(
                    property.PropertyType == typeof(Guid) || property.PropertyType == typeof(string),
                    $"{payloadType.Name}.{property.Name} must be a Guid id or a string state name (identifier-only, threat T7), not {property.PropertyType.Name}");
            }

            // Serialize with the SAME default options the command sites use, then assert the JSON field names
            // are EXACTLY the record's CLR (PascalCase) property names — proving the wire shape the TypeScript
            // payload contracts mirror, with no extra or renamed field.
            var instance = Activator.CreateInstance(payloadType, SampleConstructorArguments(payloadType));
            var json = JsonSerializer.Serialize(instance, payloadType);

            using var document = JsonDocument.Parse(json);
            var jsonFields = document.RootElement
                .EnumerateObject()
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);
            var clrFields = properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            Assert.True(
                jsonFields.SetEquals(clrFields),
                $"{eventType} payload serialized to [{string.Join(", ", jsonFields)}] but its contract is [{string.Join(", ", clrFields)}]");
        }
    }

    /// <summary>
    /// Builds positional constructor arguments for a payload record from generic sample values: a fresh Guid
    /// for each id, a placeholder name for each string. Used only to obtain a serializable instance.
    /// </summary>
    private static object?[] SampleConstructorArguments(Type payloadType)
    {
        var constructor = payloadType.GetConstructors().Single();
        return constructor
            .GetParameters()
            .Select(parameter => parameter.ParameterType == typeof(Guid)
                ? (object)Guid.NewGuid()
                : "Sample")
            .ToArray();
    }
}
