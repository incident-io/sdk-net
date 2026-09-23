using System;
using System.Net.Http;
using System.Reflection;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;

namespace IncidentIo;

// The generated half of this class, in Generated/IncidentIoClient.cs, only
// takes a Kiota IRequestAdapter. This half adds the constructor most callers
// want: an API key in, a configured client out.
public partial class IncidentIoClient
{
    /// <summary>The production API, used unless <see cref="IncidentIoClientOptions.BaseUrl"/> says otherwise.</summary>
    public const string DefaultBaseUrl = "https://api.incident.io";

    /// <summary>
    /// Creates a client that authenticates with an incident.io API key.
    /// </summary>
    /// <param name="apiKey">An API key, created under Settings → API keys in the incident.io dashboard.</param>
    /// <param name="options">Optional settings, such as a different base URL.</param>
    /// <remarks>
    /// The client holds an <see cref="HttpClient"/>, so create one and reuse it
    /// rather than creating one per request.
    /// </remarks>
    public IncidentIoClient(string apiKey, IncidentIoClientOptions? options = null)
        : this(CreateRequestAdapter(apiKey, options ?? new IncidentIoClientOptions()))
    {
    }

    private static HttpClientRequestAdapter CreateRequestAdapter(string apiKey, IncidentIoClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(apiKey));
        }

        var baseUrl = options.BaseUrl.TrimEnd('/');

        // The key is only attached to requests for the configured host, so a
        // WithUrl call pointing anywhere else doesn't leak it.
        var auth = new ApiKeyAuthenticationProvider(
            "Bearer " + apiKey,
            "Authorization",
            ApiKeyAuthenticationProvider.KeyLocation.Header,
            new Uri(baseUrl).Host);

        // Kiota's default middleware: retries that honour Retry-After, redirects,
        // and the User-Agent product token set here.
        var userAgent = new UserAgentHandlerOption
        {
            ProductName = "incident-io-sdk-dotnet",
            ProductVersion = SdkVersion(),
        };
        var httpClient = KiotaClientFactory.Create(options.InnerHandler, new IRequestOption[] { userAgent });

        return new HttpClientRequestAdapter(auth, httpClient: httpClient) { BaseUrl = baseUrl };
    }

    // Read from the assembly, which dotnet pack stamps with the release version,
    // so it always matches the published package without a hand-kept constant.
    private static string SdkVersion() =>
        typeof(IncidentIoClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
}

/// <summary>Settings for <see cref="IncidentIoClient(string, IncidentIoClientOptions?)"/>.</summary>
public sealed class IncidentIoClientOptions
{
    /// <summary>
    /// The API to send requests to. Defaults to <see cref="IncidentIoClient.DefaultBaseUrl"/>.
    /// Must be https: the API key is never sent over plain http.
    /// </summary>
    public string BaseUrl { get; set; } = IncidentIoClient.DefaultBaseUrl;

    /// <summary>
    /// The handler that sends each request, after the retry, redirect and
    /// User-Agent middleware. Set it to configure a proxy or TLS, or to
    /// intercept requests in tests. Defaults to Kiota's default handler.
    /// </summary>
    public HttpMessageHandler? InnerHandler { get; set; }
}
