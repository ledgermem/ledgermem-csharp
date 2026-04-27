using System.Text.Json.Serialization;

namespace LedgerMem;

public sealed record Memory(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("metadata")] Dictionary<string, object>? Metadata,
    [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt
);

public sealed record SearchHit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("metadata")] Dictionary<string, object>? Metadata
);

public sealed record SearchResponse(
    [property: JsonPropertyName("hits")] IReadOnlyList<SearchHit> Hits
);

public sealed record ListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<Memory> Data,
    [property: JsonPropertyName("nextCursor")] string? NextCursor
);

public sealed class LedgerMemException : Exception
{
    public int StatusCode { get; }

    public LedgerMemException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}
