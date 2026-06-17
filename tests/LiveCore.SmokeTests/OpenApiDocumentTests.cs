// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.SmokeTests;

/// <summary>
/// Build tests for the v1 OpenAPI document (CORE-OAS-001) over the real production <c>Program</c>.
///
/// <para>
/// They pin the acceptance criteria: the API produces an OpenAPI 3 document from the running route table that
/// describes exactly the registered <c>/api/v1</c> routes plus the Problem Details error shape, it is served at
/// a documented endpoint ONLY outside Production, and the committed <c>openapi/livecore-v1.json</c> build
/// artifact is that generated document (not hand-maintained). The route-coverage check is the in-process
/// counterpart of spec-consistency check 6 (the PowerShell drift gate compares the committed document to the
/// minimal-API registrations); here it compares the served document to the host's own
/// <see cref="EndpointDataSource"/>, so a route added without the document regenerated fails CI.
/// </para>
///
/// <para>
/// To regenerate the committed document after an intentional route/schema change, run the suite with
/// <c>LIVECORE_OPENAPI_UPDATE=1</c> set; the <see cref="Committed_document_matches_the_generated_document"/>
/// test rewrites <c>openapi/livecore-v1.json</c> from the live document and then asserts the match.
/// </para>
/// </summary>
public class OpenApiDocumentTests
{
    private const string _documentRoute = "/openapi/v1.json";

    private static readonly string[] _httpMethods =
        ["get", "put", "post", "delete", "patch", "options", "head", "trace"];

    private static WebApplicationFactory<Program> CreateFactory(string environment)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment(environment));

    [Fact]
    public async Task Generated_document_is_served_in_non_production_and_is_valid_openapi3()
    {
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(_documentRoute);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // The OpenAPI 3 structural contract: a 3.x version, an info object, a non-empty paths object whose
        // every operation declares responses, and a components/schemas section.
        var version = root.GetProperty("openapi").GetString();
        Assert.StartsWith("3.", version);

        var info = root.GetProperty("info");
        Assert.False(string.IsNullOrWhiteSpace(info.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(info.GetProperty("version").GetString()));

        var paths = root.GetProperty("paths");
        Assert.NotEmpty(paths.EnumerateObject());
        foreach (var path in paths.EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (!_httpMethods.Contains(operation.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // OpenAPI 3 requires every operation to declare a non-empty responses object.
                Assert.True(
                    operation.Value.TryGetProperty("responses", out var responses)
                    && responses.EnumerateObject().Any(),
                    $"operation {operation.Name.ToUpperInvariant()} {path.Name} declares no responses");
            }
        }

        // Every internal $ref resolves to a declared component schema (no dangling references).
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var declared = schemas.EnumerateObject().Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var reference in FindSchemaReferences(root))
        {
            Assert.Contains(reference, declared);
        }
    }

    [Fact]
    public async Task Generated_document_describes_the_problem_details_error_shape()
    {
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync(_documentRoute);
        using var document = JsonDocument.Parse(json);

        var problem = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ProblemDetails");

        // The RFC 7807 shape plus the stable machine-readable code extension (CORE-DX-001).
        var properties = problem.GetProperty("properties");
        foreach (var member in new[] { "type", "title", "status", "detail", "instance", "code" })
        {
            Assert.True(properties.TryGetProperty(member, out _), $"ProblemDetails is missing '{member}'");
        }
    }

    [Fact]
    public async Task Generated_document_does_not_leak_internal_commentary_into_schemas()
    {
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync(_documentRoute);

        // The request DTOs carry internal XML doc rationale (threat-model references, story ids, docs/ paths);
        // the document transformer strips all such prose, so none of it may appear in the published contract
        // (the story note "do NOT leak content in schemas, T7"). The document must also stay ASCII-clean.
        foreach (var marker in new[] { "threat", "docs/", "CORE-", "T7", "T5", "hidden-404", "Idempotency-Key" })
        {
            Assert.DoesNotContain(marker, json, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(json, c => c > '~');
    }

    [Fact]
    public async Task Generated_document_covers_exactly_the_registered_api_v1_routes()
    {
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync(_documentRoute);

        var documented = DocumentedRouteKeys(json);
        var registered = RegisteredApiV1RouteKeys(factory.Services);

        var undocumented = registered.Except(documented).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var phantom = documented.Except(registered).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.True(
            undocumented.Length == 0,
            "the OpenAPI document is missing registered /api/v1 routes: " + string.Join(", ", undocumented));
        Assert.True(
            phantom.Length == 0,
            "the OpenAPI document lists routes no endpoint mounts: " + string.Join(", ", phantom));
    }

    [Fact]
    public async Task Document_is_not_served_in_production()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();

        // The explorer endpoint is mapped only outside Production (the story note "keep any explorer
        // non-production only"), so a production deployment exposes no schema-discovery surface.
        var response = await client.GetAsync(_documentRoute);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Committed_document_matches_the_generated_document()
    {
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();

        var liveJson = await client.GetStringAsync(_documentRoute);
        var committedPath = CommittedDocumentPath();

        // Opt-in regeneration: rewrite the committed artifact from the live document so an intentional
        // route/schema change is captured by re-running with LIVECORE_OPENAPI_UPDATE=1.
        if (string.Equals(Environment.GetEnvironmentVariable("LIVECORE_OPENAPI_UPDATE"), "1", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(committedPath)!);
            await File.WriteAllTextAsync(committedPath, liveJson);
        }

        Assert.True(
            File.Exists(committedPath),
            $"committed OpenAPI document not found at {committedPath}; regenerate it with LIVECORE_OPENAPI_UPDATE=1");

        var committedJson = await File.ReadAllTextAsync(committedPath);

        // Compare canonically (recursively key-sorted, whitespace-insensitive) so the committed artifact is
        // proven to be the generated document, robust to incidental formatting differences.
        Assert.True(
            Canonicalize(committedJson) == Canonicalize(liveJson),
            "the committed openapi/livecore-v1.json has drifted from the generated document; "
            + "regenerate it with LIVECORE_OPENAPI_UPDATE=1 set");
    }

    private static IEnumerable<string> FindSchemaReferences(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("$ref") && property.Value.ValueKind == JsonValueKind.String)
                {
                    var reference = property.Value.GetString();
                    const string prefix = "#/components/schemas/";
                    if (reference is not null && reference.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        yield return reference[prefix.Length..];
                    }
                }

                foreach (var nested in FindSchemaReferences(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in FindSchemaReferences(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static HashSet<string> DocumentedRouteKeys(string json)
    {
        using var document = JsonDocument.Parse(json);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (_httpMethods.Contains(operation.Name, StringComparer.OrdinalIgnoreCase))
                {
                    keys.Add(RouteKey(operation.Name, path.Name));
                }
            }
        }

        return keys;
    }

    private static HashSet<string> RegisteredApiV1RouteKeys(IServiceProvider services)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in services.GetServices<EndpointDataSource>())
        {
            foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
            {
                var rawPath = endpoint.RoutePattern.RawText;
                if (rawPath is null || !rawPath.StartsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                if (methods is null)
                {
                    continue;
                }

                foreach (var method in methods)
                {
                    keys.Add(RouteKey(method, rawPath));
                }
            }
        }

        return keys;
    }

    private static string RouteKey(string method, string path)
    {
        // The semantic identity of a route is its method plus its path shape: parameter names
        // ({sessionId} vs {id}) and route constraints describe the same route, so normalize them away. This
        // mirrors ConvertTo-LiveCoreRouteKey in LiveCoreSpecConsistency.psm1.
        var normalized = Regex.Replace(path, "\\{[^}]*\\}", "{}");
        if (normalized.Length > 1)
        {
            normalized = normalized.TrimEnd('/');
        }

        return $"{method.ToUpperInvariant()} {normalized}";
    }

    private static string CommittedDocumentPath()
        => Path.Combine(RepositoryRoot(), "openapi", "livecore-v1.json");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LiveCore.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (LiveCore.slnx) from the test directory.");
    }

    private static string Canonicalize(string json)
    {
        var node = JsonNode.Parse(json);
        var canonical = CanonicalizeNode(node);
        return canonical?.ToJsonString() ?? "null";
    }

    private static JsonNode? CanonicalizeNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    sorted[pair.Key] = CanonicalizeNode(pair.Value?.DeepClone());
                }

                return sorted;
            case JsonArray array:
                var result = new JsonArray();
                foreach (var item in array)
                {
                    result.Add(CanonicalizeNode(item?.DeepClone()));
                }

                return result;
            default:
                return node?.DeepClone();
        }
    }
}
