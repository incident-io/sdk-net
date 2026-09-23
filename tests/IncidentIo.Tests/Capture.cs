using System.Net;
using System.Text;

namespace IncidentIo.Tests;

/// <summary>
/// Stands in for the network: records every request and answers with a fixed
/// response. Passed as <see cref="IncidentIoClientOptions.InnerHandler"/>, so
/// requests still go through the auth, User-Agent and retry middleware the
/// tests exist to check.
/// </summary>
internal sealed class Capture(string body = "{}", HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json")
    : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();

    public HttpRequestMessage Last => Requests[^1];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        });
    }

    public IncidentIoClient Client(string apiKey = "test-key", string? baseUrl = null) =>
        new(apiKey, new IncidentIoClientOptions
        {
            BaseUrl = baseUrl ?? IncidentIoClient.DefaultBaseUrl,
            InnerHandler = this,
        });
}
