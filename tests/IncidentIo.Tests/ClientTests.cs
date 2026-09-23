using System.Net;
using IncidentIo.Models;
using Xunit;

namespace IncidentIo.Tests;

public class ClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SendsTheApiKeyAndUserAgentToTheApi()
    {
        var capture = new Capture("""{"severities": []}""");

        await capture.Client().V1.Severities.GetAsync(cancellationToken: Ct);

        Assert.Equal("https://api.incident.io/v1/severities", capture.Last.RequestUri!.ToString());
        Assert.Equal("Bearer test-key", capture.Last.Headers.Authorization!.ToString());
        Assert.Contains(capture.Last.Headers.UserAgent, p => p.Product?.Name == "incident-io-sdk-dotnet");
    }

    [Fact]
    public async Task UsesTheConfiguredBaseUrl()
    {
        var capture = new Capture("""{"severities": []}""");

        await capture.Client(baseUrl: "https://staging.example.com/").V1.Severities.GetAsync(cancellationToken: Ct);

        Assert.Equal("https://staging.example.com/v1/severities", capture.Last.RequestUri!.ToString());
        Assert.Equal("Bearer test-key", capture.Last.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task DoesNotSendTheApiKeyToAnotherHost()
    {
        var capture = new Capture("""{"severities": []}""");

        await capture.Client().V1.Severities.WithUrl("https://example.com/v1/severities").GetAsync(cancellationToken: Ct);

        Assert.Null(capture.Last.Headers.Authorization);
    }

    [Fact]
    public void RejectsAMissingApiKey()
    {
        Assert.Throws<ArgumentException>(() => new IncidentIoClient(" "));
    }

    [Fact]
    public async Task DeserialisesResponses()
    {
        var capture = new Capture("""
            {"severities": [{"id": "01FCNDV6P870EA6S7TK1DSYDG0", "name": "Minor", "description": "Issues with low impact.",
              "rank": 1, "created_at": "2021-08-17T13:28:57.801578Z", "updated_at": "2021-08-17T13:28:57.801578Z"}]}
            """);

        var result = await capture.Client().V1.Severities.GetAsync(cancellationToken: Ct);

        var severity = Assert.Single(result!.Severities!);
        Assert.Equal("Minor", severity.Name);
        Assert.Equal(DateTimeOffset.Parse("2021-08-17T13:28:57.801578Z"), severity.CreatedAt);
    }

    [Fact]
    public async Task ThrowsErrorResponseOnADocumentedError()
    {
        var capture = new Capture(
            """{"type": "validation_error", "status": 422, "request_id": "req-123", "errors": [{"code": "invalid_value", "message": "bad"}]}""",
            HttpStatusCode.UnprocessableEntity);

        var error = await Assert.ThrowsAsync<ErrorResponse>(() => capture.Client().V1.Severities.GetAsync(cancellationToken: Ct));

        Assert.Equal(422, error.ResponseStatusCode);
        Assert.Equal("req-123", error.RequestId);
        Assert.Equal("bad", Assert.Single(error.Errors!).Message);
    }

    [Fact]
    public async Task ReadsAnEnumValueAddedAfterThisBuildAsNull()
    {
        var capture = new Capture("""{"user": {"id": "u1", "name": "Lisa", "role": "a_role_added_later"}}""");

        var result = await capture.Client().V2.Users["u1"].GetAsync(cancellationToken: Ct);

        Assert.Equal("Lisa", result!.User!.Name);
        Assert.Null(result.User.Role);
    }

    [Fact]
    public async Task ReadsAKnownEnumValue()
    {
        var capture = new Capture("""{"user": {"id": "u1", "name": "Lisa", "role": "responder"}}""");

        var result = await capture.Client().V2.Users["u1"].GetAsync(cancellationToken: Ct);

        Assert.Equal(UserWithRolesV2Role.Responder, result!.User!.Role);
    }

    [Fact]
    public async Task ReturnsTheCsvDownloadAsAStream()
    {
        var capture = new Capture("user,hours\nlisa,3\n", contentType: "text/csv");

        await using var stream = await capture.Client().V2.PayReports["report-1"].Download.GetAsync(cancellationToken: Ct);

        using var reader = new StreamReader(stream!);
        Assert.Equal("user,hours\nlisa,3\n", await reader.ReadToEndAsync(Ct));
    }
}
