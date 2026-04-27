using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;

namespace LedgerMem;

public sealed class LedgerMemClient : IDisposable
{
    private const string DefaultBaseUrl = "https://api.proofly.dev";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public LedgerMemClient(string apiKey, string workspaceId, string? baseUrl = null, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("apiKey is required", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("workspaceId is required", nameof(workspaceId));

        var url = baseUrl
            ?? Environment.GetEnvironmentVariable("LEDGERMEM_API_URL")
            ?? DefaultBaseUrl;

        if (httpClient is null)
        {
            _http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") };
            _ownsHttpClient = true;
        }
        else
        {
            _http = httpClient;
            _http.BaseAddress ??= new Uri(url.TrimEnd('/') + "/");
            _ownsHttpClient = false;
        }

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Add("x-workspace-id", workspaceId);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ledgermem-dotnet/0.1.0");
    }

    public async Task<SearchResponse> SearchAsync(string query, int? limit = null, string? actorId = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["query"] = query };
        if (limit.HasValue) body["limit"] = limit.Value;
        if (actorId is not null) body["actorId"] = actorId;
        return await PostAsync<SearchResponse>("v1/search", body, ct).ConfigureAwait(false);
    }

    public async Task<Memory> CreateAsync(string content, IDictionary<string, object>? metadata = null, string? actorId = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["content"] = content };
        if (metadata is not null) body["metadata"] = metadata;
        if (actorId is not null) body["actorId"] = actorId;
        return await PostAsync<Memory>("v1/memories", body, ct).ConfigureAwait(false);
    }

    public async Task<Memory> UpdateAsync(string id, string? content = null, IDictionary<string, object>? metadata = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>();
        if (content is not null) body["content"] = content;
        if (metadata is not null) body["metadata"] = metadata;
        return await SendAsync<Memory>(HttpMethod.Patch, $"v1/memories/{Uri.EscapeDataString(id)}", body, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"v1/memories/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
    }

    public async Task<ListResponse> ListAsync(int? limit = null, string? cursor = null, string? actorId = null, CancellationToken ct = default)
    {
        var qs = HttpUtility.ParseQueryString(string.Empty);
        if (limit.HasValue) qs["limit"] = limit.Value.ToString();
        if (cursor is not null) qs["cursor"] = cursor;
        if (actorId is not null) qs["actorId"] = actorId;
        var path = qs.Count == 0 ? "v1/memories" : $"v1/memories?{qs}";
        using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<ListResponse>(JsonOptions, ct).ConfigureAwait(false))!;
    }

    private Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
        => SendAsync<T>(HttpMethod.Post, path, body, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new LedgerMemException((int)resp.StatusCode, $"LedgerMem API error {(int)resp.StatusCode}: {body}");
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
