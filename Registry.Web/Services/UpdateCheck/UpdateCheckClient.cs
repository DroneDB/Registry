#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>
/// The HTTPS exchange with the update check service: one POST of the payload, the answer read up to
/// <see cref="MaxResponseBytes"/>. It uses its own named client (<see cref="HttpClientName"/>): no redirect is followed
/// (any 3xx is a failure), the time is bounded, and, unlike the clients that fetch what users point at, it does not refuse
/// private addresses, since an administrator chooses the endpoint (a staging service may be on a private network).
/// </summary>
public sealed class UpdateCheckClient
{
    /// <summary>The name of the <see cref="HttpClient"/> of the update check.</summary>
    public const string HttpClientName = "update-check";

    /// <summary>The largest answer read; a larger one is a failure.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>The time the whole exchange may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _clients;
    private readonly UpdateCheckOptions _options;
    private readonly TimeProvider _time;

    public UpdateCheckClient(IHttpClientFactory clients, IOptions<UpdateCheckOptions> options, TimeProvider time)
    {
        _clients = clients;
        _options = options.Value;
        _time = time;
    }

    /// <summary>Posts <paramref name="payload"/> and returns the answer.</summary>
    /// <exception cref="UpdateCheckException">The endpoint is not HTTPS, the exchange failed or timed out, the status is not 2xx, or the answer is too large or not usable.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<UpdateCheckResponse> PostAsync(UpdateCheckPayload payload, CancellationToken ct)
    {
        if (!Uri.TryCreate(_options.UpdateCheckEndpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new UpdateCheckException("The update check endpoint is not an HTTPS URL");

        // The client's own timeout ends when the headers arrive: this one bounds the reading of the answer too
        using var timeout = new CancellationTokenSource(Timeout, _time);
        using var exchange = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            using var client = _clients.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(ApiJson.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json)
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, exchange.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new UpdateCheckException($"The update check service answered {(int)response.StatusCode}");

            return UpdateCheckResponseParser.Parse(await ReadCappedAsync(response.Content, exchange.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateCheckException($"The update check service did not answer within {Timeout.TotalSeconds:0} seconds");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateCheckException("The update check service could not be reached", ex);
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
            throw new UpdateCheckException("The answer of the update check service is too large");

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw new UpdateCheckException("The answer of the update check service is too large");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
