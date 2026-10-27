#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>
/// One round trip with the update check service: the payload this node sends (<see cref="UpdateCheckPayloadBuilder"/>) and
/// the answer it gets (<see cref="UpdateCheckClient"/>).
/// </summary>
public sealed class UpdateCheckExchange
{
    private readonly UpdateCheckPayloadBuilder _payloads;
    private readonly UpdateCheckClient _client;

    public UpdateCheckExchange(UpdateCheckPayloadBuilder payloads, UpdateCheckClient client)
    {
        _payloads = payloads;
        _client = client;
    }

    /// <summary>Builds the payload of this node, posts it and returns the answer.</summary>
    /// <exception cref="UpdateCheckException">The exchange failed (see <see cref="UpdateCheckClient.PostAsync"/>).</exception>
    /// <exception cref="StorageDocumentException">The identity of the deployment could not be stored.</exception>
    /// <exception cref="InvalidOperationException">This build has no version the request can carry.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public Task<UpdateCheckResponse> RunAsync(CancellationToken ct) => _client.PostAsync(_payloads.Build(), ct);
}
