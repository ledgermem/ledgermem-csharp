using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Mnemo.Tests;

public class MnemoClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public required Func<HttpRequestMessage, HttpResponseMessage> Responder { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return Responder(request);
        }
    }

    private static (MnemoClient client, StubHandler handler) MakeClient(HttpResponseMessage response)
    {
        var handler = new StubHandler { Responder = _ => response };
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };
        var client = new MnemoClient("test-key", "ws_123", baseUrl: "https://api.test", httpClient: http);
        return (client, handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, object body) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task SearchAsync_SendsBearerAndWorkspaceHeaders()
    {
        var (client, handler) = MakeClient(Json(HttpStatusCode.OK, new { hits = new[] { new { id = "m1", content = "hi", score = 0.9 } } }));

        var result = await client.SearchAsync("hello", limit: 3);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Equal("ws_123", handler.LastRequest.Headers.GetValues("x-workspace-id").Single());
        Assert.Single(result.Hits);
        Assert.Equal("m1", result.Hits[0].Id);
        Assert.Contains("\"query\":\"hello\"", handler.LastBody);
        Assert.Contains("\"limit\":3", handler.LastBody);
    }

    [Fact]
    public async Task CreateAsync_PostsContentAndReturnsMemory()
    {
        var (client, handler) = MakeClient(Json(HttpStatusCode.OK, new { id = "m_42", content = "remember", metadata = (object?)null, createdAt = "2026-01-01T00:00:00Z" }));

        var memory = await client.CreateAsync("remember");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/v1/memories", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("m_42", memory.Id);
    }

    [Fact]
    public async Task DeleteAsync_ThrowsOnNon2xx()
    {
        var (client, _) = MakeClient(Json(HttpStatusCode.NotFound, new { error = "not found" }));

        var ex = await Assert.ThrowsAsync<MnemoException>(() => client.DeleteAsync("missing"));
        Assert.Equal(404, ex.StatusCode);
    }
}
