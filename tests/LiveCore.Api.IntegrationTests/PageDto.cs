namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Test-side mirror of the server's generic <c>items + hasMore</c> page envelope
/// (<see cref="LiveCore.Api.PageResponse{T}"/>, CORE-DX-003) for deserializing a bounded list response. Every
/// list endpoint returns this shape (offset/limit/hasMore/items) rather than a bare array, so the integration
/// tests read the page and assert over its <see cref="Items"/>, <see cref="HasMore"/> and clamped
/// <see cref="Limit"/>.
/// </summary>
internal sealed record PageDto<T>(int Offset, int Limit, bool HasMore, IReadOnlyList<T> Items);
