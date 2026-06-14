using System.Text.Json;
using LiveCore.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="ConcurrencyConflictMiddleware"/> (CORE-CONC-001), which
/// translates the <see cref="DbUpdateConcurrencyException"/> a stale optimistic-
/// concurrency write throws into a fail-closed HTTP <c>409 Conflict</c> for every
/// endpoint, while leaving all other behavior untouched.
/// </summary>
public sealed class ConcurrencyConflictMiddlewareTests
{
    [Fact]
    public async Task A_concurrency_conflict_is_translated_to_a_409_problem_response()
    {
        var middleware = new ConcurrencyConflictMiddleware(
            _ => throw new DbUpdateConcurrencyException("The row was modified concurrently."));
        var context = NewHttpContext();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType);

        using var document = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Equal(StatusCodes.Status409Conflict, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Conflict", document.RootElement.GetProperty("title").GetString());

        // The detail names no resource, tenant or internal state — only the generic
        // concurrency reason (threat T7).
        var detail = document.RootElement.GetProperty("detail").GetString();
        Assert.Contains("concurrent request", detail);
    }

    [Fact]
    public async Task A_non_concurrency_exception_propagates_unchanged()
    {
        var middleware = new ConcurrencyConflictMiddleware(
            _ => throw new InvalidOperationException("unrelated failure"));
        var context = NewHttpContext();

        // Only DbUpdateConcurrencyException is handled; everything else keeps the
        // host's existing error behavior.
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
        Assert.NotEqual(StatusCodes.Status409Conflict, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_successful_request_is_left_untouched()
    {
        var middleware = new ConcurrencyConflictMiddleware(context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        var httpContext = NewHttpContext();

        await middleware.InvokeAsync(httpContext);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
    }

    private static DefaultHttpContext NewHttpContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            // Minimal service provider so the problem-details result can resolve the
            // logger/JSON services it writes through, exactly as the real pipeline does.
            RequestServices = services.BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
