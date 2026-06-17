// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for response compression (CORE-PERF-006, the "Performance and Scalability" epic). They
/// drive the REAL application over real HTTP (<see cref="WorkspaceApiFactory"/>) and assert the acceptance
/// criteria and the story's required tests:
/// <list type="bullet">
///   <item>a LARGE JSON list response (a workspace's sessions) is returned COMPRESSED when the client advertises
///   <c>Accept-Encoding: br</c> (and <c>gzip</c>), and the DECOMPRESSED body is byte-for-byte identical to the
///   uncompressed response — so compression changes only transfer encoding, never content;</item>
///   <item>the SignalR hub transport is NOT compressed: an authenticated <c>/hubs/session/negotiate</c> returns
///   <c>200</c> JSON with NO <c>Content-Encoding</c> even when the client accepts compression, so the hub's own
///   transport framing is never double-compressed;</item>
///   <item>small / non-JSON / streaming-adjacent responses behave correctly: a request with no
///   <c>Accept-Encoding</c> is served uncompressed, the small <c>/api/v1/me</c> response round-trips to the same
///   content, and the non-JSON Prometheus <c>/metrics</c> text is never compressed;</item>
///   <item>the feature is configurable: <c>ResponseCompression:Enabled=false</c> serves the same large list
///   uncompressed.</item>
/// </list>
///
/// The WebApplicationFactory client does not auto-decompress (its handler leaves
/// <see cref="System.Net.Http.HttpClientHandler.AutomaticDecompression"/> off), so a compressed response arrives
/// with its <c>Content-Encoding</c> header intact and the test decompresses it explicitly. Compression is a
/// coarse bandwidth optimization layered on the OIDC/tenant authorization every endpoint already enforces; it
/// never widens it. All fixtures are generic Core vocabulary (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class ResponseCompressionEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    // Enough sessions that the serialized JSON array is comfortably large and visibly compressible.
    private const int _sessionCount = 60;

    // ====================================================================
    // A large JSON list is compressed when the client accepts it, and the
    // decompressed body is identical to the uncompressed response.
    // ====================================================================

    [Fact]
    public async Task A_large_json_list_is_returned_brotli_compressed_when_the_client_accepts_it()
    {
        await using var factory = new WorkspaceApiFactory();
        var (subject, workspaceId) = await SeedWorkspaceWithSessionsAsync(factory, "host-a");
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var compressed = await GetSessionsAsync(client, workspaceId, acceptEncoding: "br");
        var plain = await GetSessionsAsync(client, workspaceId, acceptEncoding: null);

        Assert.Equal(HttpStatusCode.OK, compressed.StatusCode);
        Assert.Contains("br", compressed.ContentEncoding);

        // The compressed payload is genuinely smaller than the body it encodes (real work happened on a large
        // list), and decompresses to EXACTLY the bytes the uncompressed response returned — content unchanged.
        var decoded = DecompressBrotli(compressed.Body);
        Assert.True(decoded.Length > compressed.Body.Length, "the large JSON list should compress smaller");
        Assert.Empty(plain.ContentEncoding);
        Assert.Equal(plain.Body, decoded);
        Assert.Equal(_sessionCount, CountPageItems(decoded));
    }

    [Fact]
    public async Task A_large_json_list_is_returned_gzip_compressed_when_the_client_accepts_only_gzip()
    {
        await using var factory = new WorkspaceApiFactory();
        var (subject, workspaceId) = await SeedWorkspaceWithSessionsAsync(factory, "host-a");
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var compressed = await GetSessionsAsync(client, workspaceId, acceptEncoding: "gzip");
        var plain = await GetSessionsAsync(client, workspaceId, acceptEncoding: null);

        Assert.Equal(HttpStatusCode.OK, compressed.StatusCode);
        Assert.Contains("gzip", compressed.ContentEncoding);

        var decoded = DecompressGzip(compressed.Body);
        Assert.True(decoded.Length > compressed.Body.Length, "the large JSON list should compress smaller");
        Assert.Equal(plain.Body, decoded);
    }

    // ====================================================================
    // The SignalR hub transport is excluded — never double-compressed.
    // ====================================================================

    [Fact]
    public async Task The_signalr_hub_negotiate_is_not_compressed_even_when_the_client_accepts_compression()
    {
        // An authenticated negotiate returns 200 application/json — a content type that WOULD be compressed on
        // the REST surface. Because the hub path (/hubs) is excluded from the compression branch, the negotiate
        // response carries NO Content-Encoding, so SignalR's own transport framing is never double-compressed.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClientFor("host-a", _issuer);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/hubs/session/negotiate?negotiateVersion=1");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    // ====================================================================
    // Small / non-JSON / no-negotiation responses behave correctly.
    // ====================================================================

    [Fact]
    public async Task Without_accept_encoding_the_large_list_is_served_uncompressed()
    {
        await using var factory = new WorkspaceApiFactory();
        var (subject, workspaceId) = await SeedWorkspaceWithSessionsAsync(factory, "host-a");
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var response = await GetSessionsAsync(client, workspaceId, acceptEncoding: null);

        // No negotiation, no compression: the body is plain JSON and content negotiation is honored.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.ContentEncoding);
        Assert.Equal(_sessionCount, CountPageItems(response.Body));
    }

    [Fact]
    public async Task A_small_json_response_round_trips_to_the_same_content_when_compression_is_accepted()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var compressed = await GetAsync(client, "/api/v1/me", acceptEncoding: "br");
        var plain = await GetAsync(client, "/api/v1/me", acceptEncoding: null);

        // Whether or not a small response is compressed, it must decode to exactly the same content as the
        // uncompressed response — a small response "behaves correctly".
        Assert.Equal(HttpStatusCode.OK, compressed.StatusCode);
        var decoded = compressed.ContentEncoding.Contains("br") ? DecompressBrotli(compressed.Body) : compressed.Body;
        Assert.Equal(plain.Body, decoded);
    }

    [Fact]
    public async Task The_non_json_metrics_text_is_not_compressed()
    {
        // /metrics is text/plain (Prometheus exposition), not in the JSON allow-list, so it passes through
        // uncompressed even when the client accepts compression — already-compressed/non-JSON content is left
        // untouched.
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await GetAsync(client, "/metrics", acceptEncoding: "br");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.ContentEncoding);
    }

    // ====================================================================
    // Configurable: disabling the feature serves everything uncompressed.
    // ====================================================================

    [Fact]
    public async Task Disabling_the_feature_serves_the_large_list_uncompressed()
    {
        await using var factory = new ConfiguredCompressionApiFactory(new Dictionary<string, string?>
        {
            ["ResponseCompression:Enabled"] = "false",
        });
        var (subject, workspaceId) = await SeedWorkspaceWithSessionsAsync(factory, "host-a");
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        var response = await GetSessionsAsync(client, workspaceId, acceptEncoding: "br");

        // With the feature off the middleware is not added: the client's Accept-Encoding is ignored and the
        // identical JSON is returned uncompressed.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.ContentEncoding);
        Assert.Equal(_sessionCount, CountPageItems(response.Body));
    }

    // ---- helpers ------------------------------------------------------------

    private static async Task<(string Subject, Guid WorkspaceId)> SeedWorkspaceWithSessionsAsync(
        WorkspaceApiFactory factory,
        string subject)
    {
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            workspaceId = workspace.Id;
            for (var i = 0; i < _sessionCount; i++)
            {
                await db.AddSessionAsync(
                    org.Id, workspace.Id, $"Session number {i:D3} of the summer show", SessionStatus.Prepared);
            }
        });

        return (subject, workspaceId);
    }

    // limit=200 (the server MaxLimit) returns all seeded sessions in one page, so the list is genuinely large.
    private static Task<HttpResult> GetSessionsAsync(HttpClient client, Guid workspaceId, string? acceptEncoding)
        => GetAsync(
            client,
            $"/api/v1/workspaces/{workspaceId}/sessions?organizationSlug={_orgA}&limit=200",
            acceptEncoding);

    private static async Task<HttpResult> GetAsync(HttpClient client, string path, string? acceptEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (acceptEncoding is not null)
        {
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(acceptEncoding));
        }

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();
        return new HttpResult(
            response.StatusCode,
            response.Content.Headers.ContentEncoding,
            body);
    }

    private static byte[] DecompressBrotli(byte[] compressed)
    {
        using var source = new MemoryStream(compressed);
        using var brotli = new BrotliStream(source, CompressionMode.Decompress);
        using var target = new MemoryStream();
        brotli.CopyTo(target);
        return target.ToArray();
    }

    private static byte[] DecompressGzip(byte[] compressed)
    {
        using var source = new MemoryStream(compressed);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var target = new MemoryStream();
        gzip.CopyTo(target);
        return target.ToArray();
    }

    private static int CountPageItems(byte[] json)
    {
        // The list endpoints return a bounded page envelope (CORE-DX-003): { offset, limit, hasMore, items: [...] }.
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(json));
        return document.RootElement.GetProperty("items").GetArrayLength();
    }

    private sealed record HttpResult(
        HttpStatusCode StatusCode,
        ICollection<string> ContentEncoding,
        byte[] Body);

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that layers in-memory <c>ResponseCompression:*</c> overrides on top of
    /// the production configuration, so a test can flip a toggle and observe the result over real HTTP — exactly
    /// the pattern the security-headers and rate-limiting suites use.
    /// </summary>
    private sealed class ConfiguredCompressionApiFactory(IReadOnlyDictionary<string, string?> overrides)
        : WorkspaceApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(overrides));
        }
    }
}
