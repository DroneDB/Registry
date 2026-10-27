#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Moq;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.UpdateCheck;
using Registry.Web.Utilities;

namespace Registry.Web.Test.UpdateCheck;

/// <summary>Builders shared by the update check tests.</summary>
internal static class UpdateCheckTestKit
{
    public static IOptions<StorageOptions> Storage(string folder) => Microsoft.Extensions.Options.Options.Create(new StorageOptions { StoragePath = folder });

    public static IOptions<UpdateCheckOptions> Configure(UpdateCheckOptions? options = null) =>
        Microsoft.Extensions.Options.Options.Create(options ?? new UpdateCheckOptions());

    /// <summary>An <see cref="IHttpClientFactory"/> whose clients use <paramref name="handler"/>.</summary>
    public static IHttpClientFactory Factory(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(UpdateCheckClient.HttpClientName)).Returns(() => new HttpClient(handler, disposeHandler: false));
        return factory.Object;
    }

    /// <summary>A well-formed answer of the update check service.</summary>
    public static string Answer(string latest, string notes = "## Fixed\n- A fix", bool truncated = false,
        string licenseRequestUrl = "https://dronedb.app/request-license", string version = "")
    {
        var release = version.Length == 0 ? latest : version;
        return $$"""
            {
              "latestVersion": "{{latest}}",
              "releaseDate": "2027-03-15",
              "changelogUrl": "https://github.com/DroneDB/Registry/releases/tag/v{{latest}}",
              "compareUrl": "https://github.com/DroneDB/Registry/compare/v2.7.1...v{{latest}}",
              "releases": [
                { "version": "{{release}}", "date": "2027-03-15", "url": "https://github.com/DroneDB/Registry/releases/tag/v{{release}}", "notes": {{System.Text.Json.JsonSerializer.Serialize(notes)}} }
              ],
              "releasesTruncated": {{(truncated ? "true" : "false")}},
              "licenseRequestUrl": "{{licenseRequestUrl}}"
            }
            """;
    }
}

/// <summary>Answers every request with what the test says, and records the requests.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    public StubHandler(HttpStatusCode status, string body = "", string mediaType = "application/json")
        : this((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) }))
    {
    }

    /// <summary>The requests received: method, URL, content type and body.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public int Count => Requests.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!, request.Content?.Headers.ContentType?.ToString(), body));
        return await _respond(request, cancellationToken);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? ContentType, string Body);

/// <summary>An exchange that never answers on its own.</summary>
internal static class Stall
{
    /// <summary>Completes (as canceled) only when the token is canceled: a stalled peer, without a clock.</summary>
    public static async Task UntilCanceledAsync(CancellationToken cancellationToken)
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = cancellationToken.Register(() => never.TrySetCanceled(cancellationToken));
        await never.Task;
    }
}

/// <summary>A temporary storage folder, deleted with the object.</summary>
internal sealed class TempStorage : IDisposable
{
    public TempStorage() => Folder = Directory.CreateTempSubdirectory("registry-update-check-").FullName;

    public string Folder { get; }

    public IOptions<StorageOptions> Storage => UpdateCheckTestKit.Storage(Folder);

    public string File(string name) => Path.Combine(Folder, name);

    public void Dispose()
    {
        if (Directory.Exists(Folder))
            Directory.Delete(Folder, true);
    }
}

/// <summary>A logger factory that records what its loggers write.</summary>
internal sealed class RecordingLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
{
    public System.Collections.Concurrent.ConcurrentQueue<(string Category, Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = new();

    public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider)
    {
    }

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(string category,
        System.Collections.Concurrent.ConcurrentQueue<(string, Microsoft.Extensions.Logging.LogLevel, string)> entries)
        : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((category, logLevel, formatter(state, exception)));
    }
}

/// <summary>Records what the update check tells it, and fails when the test says so.</summary>
internal sealed class RecordingNotifier : Registry.Web.Services.Ports.IUpdateNotifier
{
    public System.Collections.Generic.List<Registry.Web.Models.DTO.UpdateInfoDto> Calls { get; } = [];

    public Func<Registry.Web.Models.DTO.UpdateInfoDto, Task>? OnNotify { get; set; }

    public Exception? Failure { get; set; }

    public async Task NotifyAsync(Registry.Web.Models.DTO.UpdateInfoDto update, CancellationToken ct)
    {
        Calls.Add(update);
        if (OnNotify is not null)
            await OnNotify(update);
        if (Failure is not null)
            throw Failure;
    }
}

/// <summary>
/// An <see cref="UpdateCheckService"/> over a temporary storage folder, a controllable clock and a stub handler: the
/// update check as one node of a deployment runs it.
/// </summary>
internal sealed class UpdateCheckHarness : IDisposable
{
    public UpdateCheckHarness(StubHandler handler, UpdateCheckOptions? options = null, HttpHostOptions? http = null)
    {
        Storage = new TempStorage();
        Time = new Registry.Web.Test.Support.FakeTime(new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc));
        Handler = handler;
        Options = options ?? new UpdateCheckOptions();

        var ddb = new Mock<Registry.Ports.IDdbWrapper>();
        ddb.Setup(d => d.GetVersion()).Returns("2.0.0 1f171e5");
        Identity = new Registry.Web.Test.Support.FixedInstanceIdentity();
        Settings = new UpdateCheckSettingsStore(Storage.Storage, Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateCheckSettingsStore>.Instance);
        Observed = new ObservedBaseUrlStore(Storage.Storage, Time, Microsoft.Extensions.Logging.Abstractions.NullLogger<ObservedBaseUrlStore>.Instance);
        BaseUrl = new InstanceBaseUrl(Microsoft.Extensions.Options.Options.Create(http ?? new HttpHostOptions()), Observed);
        Cache = new UpdateCheckCacheStore(Storage.Storage, Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateCheckCacheStore>.Instance);

        var payloads = new UpdateCheckPayloadBuilder(Identity, Settings, BaseUrl, new PlatformFacts(),
            Microsoft.Extensions.Options.Options.Create(new Registry.Web.Models.Configuration.DataStoreOptions()),
            HostResourceBudget.Create(InstanceType.Default, new HostFacts(4, 8L << 30), new HostResourceOptions(), new DdbRuntimeOptions(), _ => null), ddb.Object);
        Info = new UpdateInfoBuilder(Identity, Settings, BaseUrl);
        Client = new UpdateCheckClient(Factory(handler), Microsoft.Extensions.Options.Options.Create(Options), Time);
        Logs = new RecordingLoggerFactory();
        Service = new UpdateCheckService(Microsoft.Extensions.Options.Options.Create(Options), payloads, Client, Cache, Info,
            Notifier, Time, new Microsoft.Extensions.Logging.Logger<UpdateCheckService>(Logs));
    }

    public TempStorage Storage { get; }
    public Registry.Web.Test.Support.FakeTime Time { get; }
    public StubHandler Handler { get; }
    public UpdateCheckOptions Options { get; }
    public Registry.Web.Services.Ports.IInstanceIdentity Identity { get; }
    public UpdateCheckSettingsStore Settings { get; }
    public ObservedBaseUrlStore Observed { get; }
    public InstanceBaseUrl BaseUrl { get; }
    public UpdateCheckCacheStore Cache { get; }
    public UpdateInfoBuilder Info { get; }
    public UpdateCheckClient Client { get; }
    public RecordingLoggerFactory Logs { get; }
    public RecordingNotifier Notifier { get; } = new();
    public UpdateCheckService Service { get; }

    private static IHttpClientFactory Factory(HttpMessageHandler handler) => UpdateCheckTestKit.Factory(handler);

    public void Dispose() => Storage.Dispose();
}
