using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;

namespace Mnemo;

public sealed class MnemoClient : IDisposable
{
    private const string DefaultBaseUrl = "https://api.getmnemo.xyz";
    private const int DefaultMaxRetries = 3;
    private const int RetryBaseDelayMs = 200;
    private const int RetryMaxDelayMs = 5_000;

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly Uri _baseUri;
    private readonly string _apiKey;
    private readonly string _workspaceId;
    private readonly int _maxRetries;
    private const string SdkUserAgent = "getmnemo-dotnet/0.1.0";
    private static readonly Random _jitter = new();
    private static readonly object _jitterLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public MnemoClient(string apiKey, string workspaceId, string? baseUrl = null, HttpClient? httpClient = null, int maxRetries = DefaultMaxRetries)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("apiKey is required", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("workspaceId is required", nameof(workspaceId));

        var url = baseUrl
            ?? Environment.GetEnvironmentVariable("GETMNEMO_API_URL")
            ?? DefaultBaseUrl;

        _apiKey = apiKey;
        _workspaceId = workspaceId;
        _baseUri = new Uri(url.TrimEnd('/') + "/");
        _maxRetries = Math.Max(0, maxRetries);

        if (httpClient is null)
        {
            _http = new HttpClient { BaseAddress = _baseUri };
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _http.DefaultRequestHeaders.Add("x-workspace-id", workspaceId);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(SdkUserAgent);
            _ownsHttpClient = true;
        }
        else
        {
            // Do not mutate a caller-supplied HttpClient: BaseAddress and default
            // headers may be shared across other consumers. Auth headers are set
            // per-request instead.
            _http = httpClient;
            _ownsHttpClient = false;
        }
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
        using var resp = await SendWithRetriesAsync(HttpMethod.Delete, $"v1/memories/{Uri.EscapeDataString(id)}", null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
    }

    public async Task<ListResponse> ListAsync(int? limit = null, string? cursor = null, string? actorId = null, CancellationToken ct = default)
    {
        var qs = HttpUtility.ParseQueryString(string.Empty);
        if (limit.HasValue) qs["limit"] = limit.Value.ToString();
        if (cursor is not null) qs["cursor"] = cursor;
        if (actorId is not null) qs["actorId"] = actorId;
        var path = qs.Count == 0 ? "v1/memories" : $"v1/memories?{qs}";
        using var resp = await SendWithRetriesAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<ListResponse>(JsonOptions, ct).ConfigureAwait(false))!;
    }

    private Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
        => SendAsync<T>(HttpMethod.Post, path, body, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var resp = await SendWithRetriesAsync(method, path, body, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false))!;
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        // Pre-serialize the body so we can resend it on retry without state
        // from a disposed HttpRequestMessage leaking between attempts.
        byte[]? bodyBytes = null;
        if (body is not null)
        {
            bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        }

        Exception? lastException = null;
        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            HttpResponseMessage? resp = null;
            HttpRequestMessage? req = null;
            try
            {
                req = BuildRequest(method, path);
                if (bodyBytes is not null)
                {
                    var content = new ByteArrayContent(bodyBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    req.Content = content;
                }

                resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (IsRetryableStatus(resp.StatusCode) && attempt < _maxRetries)
                {
                    var delayMs = RetryDelayFor(resp, attempt);
                    resp.Dispose();
                    req.Dispose();
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    continue;
                }
                req.Dispose();
                return resp;
            }
            catch (HttpRequestException ex) when (attempt < _maxRetries && !ct.IsCancellationRequested)
            {
                lastException = ex;
                resp?.Dispose();
                req?.Dispose();
                await Task.Delay(JitterDelay(attempt), ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (attempt < _maxRetries && !ct.IsCancellationRequested)
            {
                // Timeout (not user cancellation).
                lastException = ex;
                resp?.Dispose();
                req?.Dispose();
                await Task.Delay(JitterDelay(attempt), ct).ConfigureAwait(false);
            }
        }
        throw lastException ?? new InvalidOperationException("Mnemo: request failed");
    }

    private static bool IsRetryableStatus(System.Net.HttpStatusCode status)
    {
        var code = (int)status;
        // 501 Not Implemented is a permanent failure — retrying wastes round-trips.
        if (code == 501) return false;
        return code == 429 || (code >= 500 && code < 600);
    }

    private static int JitterDelay(int attempt)
    {
        var capped = Math.Min(RetryBaseDelayMs * (1 << Math.Min(attempt, 20)), RetryMaxDelayMs);
        // Random is not thread-safe before .NET 6 in all paths; lock to be safe.
        lock (_jitterLock)
        {
            return _jitter.Next(0, capped + 1);
        }
    }

    /// <summary>
    /// Honour the server's Retry-After header when present (delta-seconds or
    /// HTTP-date), otherwise fall back to exponential backoff with jitter.
    /// The value is capped at <see cref="RetryMaxDelayMs"/> so a hostile or
    /// misconfigured server cannot stall the client indefinitely.
    /// </summary>
    private static int RetryDelayFor(HttpResponseMessage resp, int attempt)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra is not null)
        {
            if (ra.Delta is { } delta)
            {
                var ms = (int)Math.Min(delta.TotalMilliseconds, RetryMaxDelayMs);
                return Math.Max(0, ms);
            }
            if (ra.Date is { } date)
            {
                var deltaMs = (date - DateTimeOffset.UtcNow).TotalMilliseconds;
                if (deltaMs <= 0) return 0;
                return (int)Math.Min(deltaMs, RetryMaxDelayMs);
            }
        }
        return JitterDelay(attempt);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path)
    {
        // When using a caller-supplied HttpClient with no BaseAddress, build an
        // absolute URI. Always set per-request auth headers so we never mutate
        // shared client state.
        var uri = _http.BaseAddress is null ? new Uri(_baseUri, path) : (Uri?)new Uri(path, UriKind.Relative);
        var req = new HttpRequestMessage(method, uri);
        if (_ownsHttpClient) return req;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Headers.TryAddWithoutValidation("x-workspace-id", _workspaceId);
        req.Headers.UserAgent.ParseAdd(SdkUserAgent);
        return req;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new MnemoException((int)resp.StatusCode, $"Mnemo API error {(int)resp.StatusCode}: {body}");
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
