using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Jobs;
using Registry.Web.Test.Support;
using Serilog.Core;
using Serilog.Events;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// A native call made while an HTTP request is served carries an operation id that names the request, the
/// same <c>RequestId</c> (<c>HttpContext.TraceIdentifier</c>) Serilog writes on the request's own records; a job keeps
/// its job id. One middleware sets it: the code that calls libddb passes nothing.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~RequestOperationTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class RequestOperationTests
{
    [Test]
    public void Begin_MakesTheRequestTheCurrentOperation_AndEndsWithIt()
    {
        DdbOperation.Current.ShouldBe(0UL);

        ulong id;
        using (var scope = RequestOperations.Begin("0HREQUEST:00000001"))
        {
            id = DdbOperation.Current;
            DdbOperation.Current.ShouldBe(id);
            id.ShouldNotBe(0UL);
            RequestOperations.Find(DdbOperation.Current).ShouldBe("0HREQUEST:00000001");
            RequestOperations.Find(id).ShouldBe("0HREQUEST:00000001");
        }

        DdbOperation.Current.ShouldBe(0UL);
        RequestOperations.Find(id).ShouldBeNull("a finished request is not kept");
        RequestOperations.Find(0).ShouldBeNull();
    }

    [Test]
    public async Task TheOperation_FollowsTheRequestAcrossAwaitsAndThreads()
    {
        using var scope = RequestOperations.Begin("0HREQUEST:00000002");
        var id = DdbOperation.Current;

        await Task.Yield();
        var onThePool = await Task.Run(() => DdbOperation.Current);

        DdbOperation.Current.ShouldBe(id);
        onThePool.ShouldBe(id);
    }

    [Test]
    public void IdsOfJobsAndRequests_AreNeverTheSame()
    {
        var ids = new ConcurrentBag<ulong>();
        Parallel.For(0, 200, i =>
        {
            if (i % 2 == 0)
            {
                using var request = RequestOperations.Begin("r" + i);
                ids.Add(DdbOperation.Current);
            }
            else
            {
                using var job = JobOperations.Begin("j" + i, null);
                ids.Add(job.Id);
            }
        });

        ids.Distinct().Count().ShouldBe(200);
    }

    [Test]
    public void NativeRecordOfARequest_NamesTheRequest_WithTheSamePropertyAsTheHostsRecords()
    {
        var logger = new StateLogger();
        var sink = new DdbLogSink(new SingleLoggerFactory(logger));

        using var request = RequestOperations.Begin("0HREQUEST:00000003");
        var requestOperation = DdbOperation.Current;
        // As libddb's callback calls it, from a native thread
        var native = new Thread(() => sink.Write(DdbLogLevel.Warning, "GDAL: cannot open a.tif", "gdal", requestOperation));
        native.Start();
        native.Join();

        var record = logger.Records.ShouldHaveSingleItem();
        record.ShouldContain(new KeyValuePair<string, object?>("RequestId", "0HREQUEST:00000003"));
        record.ShouldNotContain(p => p.Key == "JobId");
    }

    [Test]
    public void NativeRecordOfAJob_StillNamesTheJob_EvenInsideARequest()
    {
        var logger = new StateLogger();
        var sink = new DdbLogSink(new SingleLoggerFactory(logger));

        using var request = RequestOperations.Begin("0HREQUEST:00000004");
        using var job = JobOperations.Begin("job-7", "org/ds");
        DdbOperation.Current.ShouldBe(job.Id);
        sink.Write(DdbLogLevel.Warning, "warning", "ddb", job.Id);

        logger.Records.ShouldHaveSingleItem().ShouldContain(new KeyValuePair<string, object?>("JobId", "job-7"));

        // The request's own operation is back when the job's ends
        job.Dispose();
        RequestOperations.Find(DdbOperation.Current).ShouldBe("0HREQUEST:00000004");
    }

    [Test]
    public async Task ARequestThatCallsLibddb_GivesItsOwnRequestIdAsTheOperation()
    {
        var logs = new RecordingSink();
        using var factory = new RegistryWebFactory(
            new Dictionary<string, string> { ["Serilog:LevelSwitches:$controlSwitch"] = "Information" },
            services => services.AddSingleton<ILogEventSink>(logs));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var owner = await factory.CreateUserAsync("op-owner");
        string datasetFolder;
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
            var org = Seed.Organization(context, "op-org", ownerId: owner.Id, isPublic: false);
            var ds = Seed.Dataset(context, org, "op-ds");
            await context.SaveChangesAsync();
            datasetFolder = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get("op-org", ds.InternalRef).DatasetFolderPath;
        }

        await File.WriteAllBytesAsync(Path.Combine(datasetFolder, "photo.jpg"), [1, 2, 3]);
        factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        factory.DdbWrapper.Setup(d => d.GetEntry(datasetFolder, "photo.jpg"))
            .Returns(new Entry { Path = "photo.jpg", Hash = new string('d', 64), Type = EntryType.Image, Size = 3 });
        var seen = new ConcurrentQueue<(ulong Id, string? RequestId)>();
        factory.DdbWrapper.Setup(d => d.GenerateThumbnail(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(() =>
            {
                seen.Enqueue((DdbOperation.Current, RequestOperations.Find(DdbOperation.Current)));
                return [1, 2, 3, 4];
            });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/orgs/op-org/ds/op-ds/thumb?path=photo.jpg&size=256")
            .AsUser("op-owner");
        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var call = seen.ShouldHaveSingleItem();
        call.Id.ShouldNotBe(0UL, "the native call has an operation");
        call.RequestId.ShouldNotBeNullOrEmpty();
        // The same id Serilog writes on the records the host logged for this request
        logs.RequestIds().ShouldContain(call.RequestId);
        DdbOperation.Current.ShouldBe(0UL, "nothing leaks out of the request");
        RequestOperations.Find(call.Id).ShouldBeNull("the request ended");
    }

    private sealed class RecordingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

        public string[] RequestIds() => _events
            .Where(e => e.Properties.ContainsKey("RequestId"))
            .Select(e => e.Properties["RequestId"].ToString().Trim('"'))
            .Distinct().ToArray();
    }

    private sealed class StateLogger : ILogger
    {
        public List<IReadOnlyList<KeyValuePair<string, object?>>> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Records.Add(state as IReadOnlyList<KeyValuePair<string, object?>> ?? []);
    }

    private sealed class SingleLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }
}
