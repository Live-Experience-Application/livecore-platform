using System.Text.Json;
using LiveCore.Api.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Unit tests for <see cref="UnhandledExceptionProblemDetailsMiddleware"/> (CORE-RES-001), the global
/// last-resort handler that translates ANY unhandled exception into a fail-closed RFC 7807 Problem Details
/// <c>500</c> carrying the documented <c>internal_error</c> code (CORE-DX-001), instead of a bare framework
/// <c>500</c> — and never leaks the exception type, message or stack to the caller (threat T7).
/// </summary>
public sealed class UnhandledExceptionProblemDetailsMiddlewareTests
{
    [Fact]
    public async Task An_unhandled_exception_is_translated_to_a_500_problem_response_with_the_internal_error_code()
    {
        var middleware = new UnhandledExceptionProblemDetailsMiddleware(
            _ => throw new InvalidOperationException("super secret internal detail at /db/secret-table"),
            NullLogger<UnhandledExceptionProblemDetailsMiddleware>.Instance);
        var context = NewHttpContext();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType);

        using var document = JsonDocument.Parse(await ReadBodyAsync(context));
        Assert.Equal(StatusCodes.Status500InternalServerError, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Internal Server Error", document.RootElement.GetProperty("title").GetString());

        // The body carries the stable machine-readable code from the documented CORE-DX-001 catalog.
        Assert.Equal(
            ProblemCodes.InternalError,
            document.RootElement.GetProperty(ProblemCodes.Member).GetString());
    }

    [Fact]
    public async Task The_response_leaks_no_internal_detail_from_the_exception()
    {
        const string secret = "super secret internal detail at /db/secret-table";
        var middleware = new UnhandledExceptionProblemDetailsMiddleware(
            _ => throw new InvalidOperationException(secret),
            NullLogger<UnhandledExceptionProblemDetailsMiddleware>.Instance);
        var context = NewHttpContext();

        await middleware.InvokeAsync(context);

        // The whole serialized body must not echo the exception message, its type name or any stack frame
        // (threat T7) — only a generic title/detail and the stable code.
        var body = await ReadBodyAsync(context);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(body);
        var detail = document.RootElement.GetProperty("detail").GetString();
        Assert.Equal("An unexpected error occurred while processing the request.", detail);
    }

    [Fact]
    public async Task A_successful_request_is_left_untouched()
    {
        var middleware = new UnhandledExceptionProblemDetailsMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            NullLogger<UnhandledExceptionProblemDetailsMiddleware>.Instance);
        var context = NewHttpContext();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task An_exception_after_the_response_has_started_is_rethrown_not_half_swallowed()
    {
        // Once the status line is on the wire it cannot be rewritten, so the failure must stay loud rather than
        // being half-swallowed into a malformed body (same posture as ConcurrencyConflictMiddleware).
        var middleware = new UnhandledExceptionProblemDetailsMiddleware(
            _ => throw new InvalidOperationException("after the response started"),
            NullLogger<UnhandledExceptionProblemDetailsMiddleware>.Instance);
        var context = NewHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
    }

    [Fact]
    public async Task A_client_cancellation_is_rethrown_untranslated_not_turned_into_a_500()
    {
        // A client that goes away mid-request is not a server fault: the cancellation unwinds untranslated so it
        // is never counted as a 5xx error and the gone connection is never written to (CORE-OBS-001 / threat T7).
        var context = NewHttpContext();
        context.RequestAborted = new CancellationToken(canceled: true);

        var middleware = new UnhandledExceptionProblemDetailsMiddleware(
            _ => throw new OperationCanceledException(),
            NullLogger<UnhandledExceptionProblemDetailsMiddleware>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));
        Assert.NotEqual(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    private static DefaultHttpContext NewHttpContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            // Minimal service provider so the problem-details result can resolve the logger/JSON services it
            // writes through, exactly as the real pipeline does.
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

    /// <summary>
    /// A minimal response feature that reports <c>HasStarted = true</c>, so the middleware's "the status line
    /// is already on the wire" branch can be exercised without a live server (a write to a
    /// <see cref="MemoryStream"/> never flips <c>HasStarted</c> on a <see cref="DefaultHttpContext"/>).
    /// </summary>
    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }
}
