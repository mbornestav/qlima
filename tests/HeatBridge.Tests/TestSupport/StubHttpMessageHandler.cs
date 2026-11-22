using System.Net;
using System.Text;

namespace HeatBridge.Tests.TestSupport;

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a delegate and records every request.</summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public static StubHttpMessageHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(JsonResponse(json, status)));

    public static StubHttpMessageHandler Route(Func<HttpRequestMessage, HttpResponseMessage> route) =>
        new((request, _) => Task.FromResult(route(request)));

    public static StubHttpMessageHandler Throws(Exception exception) =>
        new((_, _) => Task.FromException<HttpResponseMessage>(exception));

    /// <summary>Never answers; only completes when the request is cancelled.</summary>
    public static StubHttpMessageHandler Hangs() =>
        new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        });

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage EventStreamResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return responder(request, cancellationToken);
    }
}

/// <summary>Minimal <see cref="IHttpClientFactory"/> handing out clients over a shared stub handler.</summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
