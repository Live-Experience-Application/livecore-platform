// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace LiveCore.Api.Hosting;

/// <summary>
/// OpenAPI document generation for the v1 API (CORE-OAS-001).
///
/// <para>
/// <c>docs/02_ARCHITECTURE.md</c> describes the TypeScript contracts as OpenAPI-derived, but the API shipped
/// no OpenAPI package and no spec, so the hand-written <c>@livecore/contracts</c> could silently drift from the
/// server. This wires <c>Microsoft.AspNetCore.OpenApi</c> to build an OpenAPI 3 document <em>from the running
/// minimal-API route table</em> — the ApiExplorer metadata of the endpoints already mapped in
/// <c>Program.cs</c> — so the document can never diverge from the routes the host actually mounts (the
/// acceptance criterion "generated from the running route table, not hand-maintained").
/// </para>
///
/// <para>
/// The document is named <c>v1</c> and SCOPED to the <c>/api/v1</c> surface (<see cref="ShouldIncludeApiV1"/>),
/// so the top-level infrastructure routes (<c>/health/*</c>, <c>/metrics</c>, <c>/source</c>, the SignalR hub
/// and the OpenAPI endpoint itself) are excluded — only the versioned product API is described. A document
/// transformer (<see cref="LiveCoreOpenApiDocumentTransformer"/>) stamps the product-neutral document
/// metadata, removes the request-derived <c>servers</c> entry (so the committed document is host-independent and
/// never echoes a deployment host — threat T7) and adds the RFC 7807 Problem Details error shape every endpoint
/// returns (CORE-DX-001) to the components, so the document describes the error contract as well as the routes.
/// </para>
///
/// <para>
/// The explorer endpoint is mapped at <c>/openapi/v1.json</c> ONLY outside Production
/// (<see cref="MapLiveCoreOpenApi"/>), so a production deployment exposes no schema-discovery surface
/// (the story note "keep any explorer non-production only"). The committed
/// <c>openapi/livecore-v1.json</c> build artifact is the same document; a CI gate (spec-consistency check 12)
/// fails when it drifts from the registered routes, and <c>OpenApiDocumentTests</c> asserts it is valid
/// OpenAPI 3 and matches the live document. The document carries only route shapes, generic schema names and
/// the Problem Details error shape — never a secret, tenant identifier or content (threat T7).
/// </para>
/// </summary>
internal static class OpenApiConfiguration
{
    /// <summary>The OpenAPI document name; also the <c>/openapi/{documentName}.json</c> route segment.</summary>
    public const string DocumentName = "v1";

    /// <summary>The path the non-production explorer endpoint is served at.</summary>
    public const string DocumentRoute = "/openapi/v1.json";

    /// <summary>
    /// Registers the OpenAPI document generator for the v1 API. The document is built from the registered
    /// endpoints' ApiExplorer metadata (no runtime reflection over the assembly), scoped to <c>/api/v1</c>.
    /// </summary>
    public static IServiceCollection AddLiveCoreOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options =>
        {
            // Emit OpenAPI 3.0 specifically (not 3.1): it is the broadest-compatibility "OpenAPI 3" target for
            // the downstream contract generator (CORE-OAS-002) and validators, and keeps the committed document
            // deterministic across SDK revisions.
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;

            // Scope the document to the versioned product API only. Every other mapped endpoint
            // (/health, /metrics, /source, the SignalR hub, the OpenAPI endpoint itself) is excluded, so the
            // document describes exactly the /api/v1 routes the spec-consistency route enumeration covers.
            options.ShouldInclude = ShouldIncludeApiV1;

            options.AddDocumentTransformer<LiveCoreOpenApiDocumentTransformer>();
        });

        return services;
    }

    /// <summary>
    /// Maps the read-only OpenAPI explorer endpoint at <see cref="DocumentRoute"/> — but ONLY outside
    /// Production, so a production deployment never exposes a schema-discovery surface (the story note "keep any
    /// explorer non-production only"). The committed build artifact remains available regardless of environment.
    /// </summary>
    public static WebApplication MapLiveCoreOpenApi(this WebApplication app)
    {
        if (!app.Environment.IsProduction())
        {
            app.MapOpenApi(DocumentRoute);
        }

        return app;
    }

    /// <summary>
    /// True when the described endpoint is a <c>/api/v1</c> route. The <c>RelativePath</c> of an
    /// <c>ApiDescription</c> has no leading slash (for example <c>api/v1/me</c>).
    /// </summary>
    internal static bool ShouldIncludeApiV1(Microsoft.AspNetCore.Mvc.ApiExplorer.ApiDescription description)
        => description.RelativePath is not null
            && description.RelativePath.StartsWith("api/v1", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Document transformer that stamps the product-neutral metadata, drops the request-derived server list and
/// adds the RFC 7807 Problem Details error shape (CORE-OAS-001). It runs after the routes are described, so it
/// only augments the generated document; it never reads a secret, tenant identifier or request body (threat T7).
/// </summary>
internal sealed class LiveCoreOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public async Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        // Product-neutral document metadata (no vertical domain language; AGPL like the rest of the platform).
        document.Info = new OpenApiInfo
        {
            Title = "LiveCore Platform API",
            Version = OpenApiConfiguration.DocumentName,
            Description =
                "OpenAPI 3 description of the product-neutral LiveCore Core Platform v1 HTTP API, generated "
                + "from the running minimal-API route table. Every error response is an RFC 7807 Problem "
                + "Details body carrying a stable machine-readable code (see the ProblemDetails schema).",
            License = new OpenApiLicense { Name = "AGPL-3.0-or-later" },
        };

        // Drop the request-derived servers entry so the committed document is host-independent and never echoes
        // the deployment host it was generated against (threat T7).
        document.Servers?.Clear();

        // Describe the RFC 7807 Problem Details error shape (CORE-DX-001) every endpoint returns, so the
        // document covers the error contract, not just the routes. The schema is generated from a small,
        // product-neutral contract type rather than hand-built, so it stays faithful to the wire shape.
        var problemSchema = await context.GetOrCreateSchemaAsync(typeof(ProblemDetailsContract), null, cancellationToken)
            .ConfigureAwait(false);

        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        document.Components.Schemas["ProblemDetails"] = problemSchema;

        // Strip every prose member (descriptions, summaries, examples) the project's XML doc comments inject
        // into the generated schemas and operations. The XML comments on the request DTOs carry internal
        // rationale - threat-model references, story ids, docs/ paths, even non-ASCII punctuation - which must
        // not leak into the published consumer contract (the story note "do NOT leak content in schemas, T7").
        // Stripping the prose leaves the full STRUCTURE intact (paths, methods, request/response schema shapes,
        // required fields, the Problem Details shape), so the document still describes the contract while
        // staying leak-free, deterministic and ASCII-clean. The required OpenAPI 3 response `description`
        // members are left untouched (they carry only standard reason phrases, never source-comment text).
        StripOperationProse(document);
        if (document.Components?.Schemas is { } schemas)
        {
            var visited = new HashSet<IOpenApiSchema>(ReferenceEqualityComparer.Instance);
            foreach (var schema in schemas.Values)
            {
                StripSchemaProse(schema, visited);
            }
        }
    }

    private static void StripOperationProse(OpenApiDocument document)
    {
        if (document.Paths is null)
        {
            return;
        }

        foreach (var pathItem in document.Paths.Values)
        {
            if (pathItem.Operations is null)
            {
                continue;
            }

            foreach (var operation in pathItem.Operations.Values)
            {
                operation.Summary = null;
                operation.Description = null;

                if (operation.Parameters is not null)
                {
                    foreach (var parameter in operation.Parameters)
                    {
                        if (parameter is OpenApiParameter concreteParameter)
                        {
                            concreteParameter.Description = null;
                        }
                    }
                }
            }
        }
    }

    private static void StripSchemaProse(IOpenApiSchema? schema, HashSet<IOpenApiSchema> visited)
    {
        if (schema is not OpenApiSchema concrete || !visited.Add(schema))
        {
            return;
        }

        concrete.Description = null;
        concrete.Example = null;

        if (concrete.Properties is not null)
        {
            foreach (var property in concrete.Properties.Values)
            {
                StripSchemaProse(property, visited);
            }
        }

        StripSchemaProse(concrete.Items, visited);
        StripSchemaProse(concrete.AdditionalProperties, visited);
        StripSchemaList(concrete.AllOf, visited);
        StripSchemaList(concrete.AnyOf, visited);
        StripSchemaList(concrete.OneOf, visited);
    }

    private static void StripSchemaList(IList<IOpenApiSchema>? schemas, HashSet<IOpenApiSchema> visited)
    {
        if (schemas is null)
        {
            return;
        }

        foreach (var schema in schemas)
        {
            StripSchemaProse(schema, visited);
        }
    }
}

// Product-neutral contract type whose ONLY purpose is to let the OpenAPI generator emit the RFC 7807 Problem
// Details error shape (CORE-DX-001) into the document; it is never instantiated at runtime. The fields mirror
// what CoreProblem produces - the RFC 7807 members plus the stable lower_snake_case `code` extension. The XML
// doc comments below become the public schema descriptions, so they are kept consumer-facing and ASCII-only
// (no internal rationale, threat references or non-ASCII punctuation leaks into the published contract).

/// <summary>An RFC 7807 Problem Details error response carrying a stable machine-readable code.</summary>
internal sealed record ProblemDetailsContract
{
    /// <summary>A URI reference that identifies the problem type.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>A short, human-readable summary of the problem type.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>The HTTP status code for this occurrence of the problem.</summary>
    [JsonPropertyName("status")]
    public int? Status { get; init; }

    /// <summary>A human-readable explanation specific to this occurrence of the problem.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>A URI reference that identifies the specific occurrence of the problem.</summary>
    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    /// <summary>A stable lower_snake_case error code; consumers branch on this, not on the prose.</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;
}
