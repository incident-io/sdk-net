# incident.io .NET SDK

[![NuGet](https://img.shields.io/nuget/v/IncidentIo)](https://www.nuget.org/packages/IncidentIo)

The official .NET client for the [incident.io](https://incident.io)
[public API](https://api-docs.incident.io/).

It is generated automatically from our published OpenAPI schema, so it tracks
the live API: there is a method for every endpoint, and a type for every
request and response.

## Install

```bash
dotnet add package IncidentIo
```

Targets .NET 8 and .NET 10.

## Quickstart

Create an API key in your incident.io dashboard under **Settings → API keys**,
then:

```csharp
using IncidentIo;

var client = new IncidentIoClient("my-api-key");

var result = await client.V2.Incidents.GetAsync(request =>
{
    request.QueryParameters.PageSize = 25;
});

foreach (var incident in result!.Incidents!)
{
    Console.WriteLine($"{incident.Reference} {incident.Name}");
}
```

The client holds an `HttpClient`, so create one and reuse it.

The methods follow the URL. `GET /v2/incidents` is `client.V2.Incidents.GetAsync()`,
`GET /v2/incidents/{id}` is `client.V2.Incidents[id].GetAsync()`, and
`POST /v2/incidents` is `client.V2.Incidents.PostAsync(body)`. Path segments are
PascalCase, so `/v2/alert_attributes` is `client.V2.AlertAttributes`.

Every method is async and takes an optional `CancellationToken`. Query
parameters are set on `request.QueryParameters`, as above. Request and response
types live in `IncidentIo.Models`.

## Pagination

List endpoints are cursor-paginated. Read the next cursor from
`PaginationMeta.After` and pass it back:

```csharp
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
```

## Filtering lists

Some list endpoints take filters in the form `field[operator]=value`, such as
`created_at[gte]=2025-01-01` or `status[one_of]=firing`. The generator can't
express these as typed parameters, so this SDK leaves them out. Pass them in the
URL with `WithUrl`, which keeps authentication and everything else about the
request:

```csharp
var alerts = await client.V2.Alerts
    .WithUrl("https://api.incident.io/v2/alerts?page_size=25&status[one_of]=firing&created_at[gte]=2025-01-01")
    .GetAsync();
```

The [API docs](https://api-docs.incident.io/) list the filters and operators
for each endpoint. Escape values with `Uri.EscapeDataString` if they can contain
`&`, `=` or spaces.

## Errors

A documented error status throws `IncidentIo.Models.ErrorResponse`, which
carries the API's error body. Anything else throws Kiota's `ApiException`, which
`ErrorResponse` derives from:

```csharp
using IncidentIo.Models;
using Microsoft.Kiota.Abstractions;

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
}
catch (ApiException error)
{
    Console.WriteLine($"HTTP {error.ResponseStatusCode}");
}
```

Quote the `RequestId` if you contact support about a failed request.

## Enum values added later

We add values to enums as a backwards-compatible change. When a response
carries a value this version of the SDK doesn't know, the property reads as
`null` rather than throwing. The rest of the response is unaffected. Upgrade the
package to see the new value.

## Configuration

```csharp
var client = new IncidentIoClient("my-api-key", new IncidentIoClientOptions
{
    // Defaults to https://api.incident.io. Must be https.
    BaseUrl = "https://api.incident.io",

    // The handler that sends each request, for a proxy or custom TLS. The SDK's
    // retry and User-Agent middleware still run in front of it.
    InnerHandler = new HttpClientHandler { Proxy = new System.Net.WebProxy("http://proxy:8080") },
});
```

Requests identify themselves with a `User-Agent` of
`incident-io-sdk-dotnet/<version>`. The API key is only sent to the configured
host.

To use your own `HttpClient`, or anything else Kiota supports, construct the
client from a request adapter instead. See the
[Kiota documentation](https://learn.microsoft.com/en-us/openapi/kiota/).

```csharp
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

var auth = new ApiKeyAuthenticationProvider(
    "Bearer my-api-key", "Authorization", ApiKeyAuthenticationProvider.KeyLocation.Header, "api.incident.io");
var adapter = new HttpClientRequestAdapter(auth, httpClient: myHttpClient);
var client = new IncidentIoClient(adapter);
```

### Retries

Requests that fail with `429`, `503` or `504` are retried up to three times,
honouring the `Retry-After` header. This is Kiota's default retry handler. The
API rate-limits at 1200 requests per minute per key.

A retried `POST` can be applied twice if the first attempt reached the server
before failing. Creating an incident or an escalation takes an
`IdempotencyKey` for this reason; set it.

### Alert events and heartbeats

`client.V2.AlertEvents.Http[id].PostAsync(...)` and
`client.V2.Heartbeat[id].Ping.PostAsync(...)` authenticate with the secret of the
alert source you are sending to, not with an API key. Create a separate client
with that secret:

```csharp
var alertSource = new IncidentIoClient(alertSourceSecret);

await alertSource.V2.AlertEvents.Http[alertSourceConfigId].PostAsync(new AlertEventsCreateHTTPPayloadV2
{
    Title = "Disk almost full",
    Status = AlertEventsCreateHTTPPayloadV2Status.Firing,
    DeduplicationKey = "disk-full-db-1",
});
```

### Deprecated endpoints

Deprecated endpoints stay available and are marked `[Obsolete]`, so the
compiler warns at the call site (`CS0618`). Don't assume a `V2` endpoint is
current: deprecations span V1 and V2. The release fails
if the schema marks an endpoint deprecated and the generated code doesn't, so
the warnings track the API.

If you build with `TreatWarningsAsErrors`, a newly deprecated endpoint fails
your build. Add `CS0618` to `WarningsNotAsErrors` if you'd rather keep it a
warning.

## Versioning

Releases are cut automatically. A job checks the published API schema hourly,
and when it has changed, regenerates this package, runs the tests, and publishes
a new **minor** version.

Two gates stand in front of that. [oasdiff](https://github.com/oasdiff/oasdiff)
compares the old and new schemas, and .NET
[package validation](https://learn.microsoft.com/en-us/dotnet/fundamentals/apicompat/package-validation/overview)
compares the public API against the last published package. If either reports a
breaking change the release stops and a human decides what to do, so a break is
never published as a minor version.

Patch versions are only cut by hand, for a fix to this package that isn't a
schema change.

## Support

Found a bug or missing something? Please
[open an issue](https://github.com/incident-io/sdk-net/issues). For questions
about the API itself, see the [API docs](https://api-docs.incident.io/).

Everything under `src/IncidentIo/Generated/` is generated, so please don't send
PRs editing it directly. See [CONTRIBUTING.md](./CONTRIBUTING.md) if you want
to work on the repo itself.

## License

MIT, see [LICENSE](./LICENSE).

The generated code is produced by [Kiota](https://github.com/microsoft/kiota),
which is licensed under MIT.
