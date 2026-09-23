using System.Net;
using IncidentIo.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Xunit;

// The README's calls don't pass a CancellationToken, and these copy them.
#pragma warning disable xUnit1051

namespace IncidentIo.Tests;

// Every code block in README.md, so the README can't drift from the API.
// Anything shown there belongs here. Each test runs the snippet against a
// Capture rather than the network. The snippet lines are the README's, apart
// from building the client on the Capture.
public class ReadmeExamples
{
    private static readonly string IncidentsPage = """
        {"incidents": [{"id": "01ABC", "reference": "INC-1", "name": "Checkout is down"}],
         "pagination_meta": {"page_size": 25}}
        """;

    [Fact]
    public async Task Quickstart()
    {
        var capture = new Capture(IncidentsPage);
        var client = capture.Client("my-api-key");

        var result = await client.V2.Incidents.GetAsync(request =>
        {
            request.QueryParameters.PageSize = 25;
        });

        foreach (var incident in result!.Incidents!)
        {
            Console.WriteLine($"{incident.Reference} {incident.Name}");
        }

        Assert.Equal("https://api.incident.io/v2/incidents?page_size=25", capture.Last.RequestUri!.ToString());
    }

    [Fact]
    public async Task Pagination()
    {
        var capture = new Capture(IncidentsPage);
        var client = capture.Client();

        string? after = null;

        do
        {
            var page = await client.V2.Incidents.GetAsync(request =>
            {
                request.QueryParameters.PageSize = 100;
                request.QueryParameters.After = after;
            });

            foreach (var incident in page!.Incidents!)
            {
                Console.WriteLine($"{incident.Reference} {incident.Name}");
            }

            after = page.PaginationMeta?.After;
        } while (after != null);

        Assert.Single(capture.Requests);
    }

    [Fact]
    public async Task FilteringLists()
    {
        var capture = new Capture("""{"alerts": [], "pagination_meta": {"page_size": 25}}""");
        var client = capture.Client();

        var alerts = await client.V2.Alerts
            .WithUrl("https://api.incident.io/v2/alerts?page_size=25&status[one_of]=firing&created_at[gte]=2025-01-01")
            .GetAsync();

        Assert.Equal(
            "https://api.incident.io/v2/alerts?page_size=25&status[one_of]=firing&created_at[gte]=2025-01-01",
            capture.Last.RequestUri!.OriginalString);
        Assert.Equal("Bearer test-key", capture.Last.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Errors()
    {
        var capture = new Capture("""{"type": "not_found", "status": 404, "request_id": "req-1", "errors": []}""", HttpStatusCode.NotFound);
        var client = capture.Client();
        var caught = false;

        try
        {
            await client.V2.Incidents["01ABC"].GetAsync();
        }
        catch (ErrorResponse error)
        {
            Console.WriteLine($"HTTP {error.ResponseStatusCode}, request {error.RequestId}");
            foreach (var detail in error.Errors ?? [])
            {
                Console.WriteLine(detail.Message);
            }
            caught = true;
        }
        catch (ApiException error)
        {
            Console.WriteLine($"HTTP {error.ResponseStatusCode}");
        }

        Assert.True(caught);
    }

    [Fact]
    public void Configuration()
    {
        var client = new IncidentIoClient("my-api-key", new IncidentIoClientOptions
        {
            // Defaults to https://api.incident.io. Must be https.
            BaseUrl = "https://api.incident.io",

            // The handler that sends each request, for a proxy or custom TLS. The SDK's
            // retry and User-Agent middleware still run in front of it.
            InnerHandler = new HttpClientHandler { Proxy = new System.Net.WebProxy("http://proxy:8080") },
        });

        Assert.NotNull(client.V2);
    }

    [Fact]
    public void OwnHttpClient()
    {
        using var myHttpClient = new HttpClient();

        var auth = new ApiKeyAuthenticationProvider(
            "Bearer my-api-key", "Authorization", ApiKeyAuthenticationProvider.KeyLocation.Header, "api.incident.io");
        var adapter = new HttpClientRequestAdapter(auth, httpClient: myHttpClient);
        var client = new IncidentIoClient(adapter);

        Assert.Equal("https://api.incident.io", adapter.BaseUrl);
    }

    [Fact]
    public async Task AlertEvents()
    {
        var capture = new Capture("""{"deduplication_key": "disk-full-db-1", "message": "Event accepted for processing", "status": "success"}""");
        var alertSourceSecret = "source-secret";
        var alertSourceConfigId = "01SOURCE";
        var alertSource = capture.Client(alertSourceSecret);

        await alertSource.V2.AlertEvents.Http[alertSourceConfigId].PostAsync(new AlertEventsCreateHTTPPayloadV2
        {
            Title = "Disk almost full",
            Status = AlertEventsCreateHTTPPayloadV2Status.Firing,
            DeduplicationKey = "disk-full-db-1",
        });

        Assert.Equal("https://api.incident.io/v2/alert_events/http/01SOURCE", capture.Last.RequestUri!.ToString());
        Assert.Equal("Bearer source-secret", capture.Last.Headers.Authorization!.ToString());
    }
}
