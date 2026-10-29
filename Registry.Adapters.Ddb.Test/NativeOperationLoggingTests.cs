using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Shouldly;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// A native call made under a <see cref="DdbOperation"/> logs with its id, on
/// whatever thread the call runs (the adapter sets libddb's per-call operation before each call),
/// and a call outside one logs with none.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
public class NativeOperationLoggingTests : TestBase
{
    private static readonly NativeDdbWrapper Wrapper = new();
    private static readonly NativeDdbRuntime Runtime = new();

    [Test]
    public async Task NativeRecords_CarryTheCurrentOperation_AcrossAwaitsAndThreads()
    {
        using var area = new TestArea(nameof(NativeRecords_CarryTheCurrentOperation_AcrossAwaitsAndThreads));
        var notARaster = Path.Combine(area.TestFolder, "not-a-raster.tif");
        File.WriteAllText(notARaster, "not a raster");
        var sink = new OperationSink();

        Runtime.SetLogSink(sink, DdbLogLevel.Warning);
        try
        {
            using (DdbOperation.Enter(9001))
            {
                Should.Throw<DdbException>(() => Wrapper.GetRasterInfo(notARaster));
                var firstThread = Environment.CurrentManagedThreadId;
                // Another pool thread, same flow: the id follows it
                await Task.Run(() => Should.Throw<DdbException>(() => Wrapper.GetRasterInfo(notARaster)));
                await Task.Yield();
                Should.Throw<DdbException>(() => Wrapper.GetRasterInfo(notARaster));
                firstThread.ShouldBeGreaterThan(0);
            }

            var inside = sink.Records.Select(r => r.OpId).ToArray();
            inside.ShouldNotBeEmpty();
            inside.ShouldAllBe(id => id == 9001UL);

            sink.Records.Clear();
            Should.Throw<DdbException>(() => Wrapper.GetRasterInfo(notARaster));
            sink.Records.ShouldNotBeEmpty();
            sink.Records.ShouldAllBe(r => r.OpId == 0UL, "outside an operation a call logs with none");
        }
        finally
        {
            Runtime.SetLogSink(null, DdbLogLevel.Info);
        }
    }

    private sealed class OperationSink : IDdbLogSink
    {
        public ConcurrentQueue<(DdbLogLevel Level, ulong OpId)> Records { get; } = new();

        public void Write(DdbLogLevel level, string message, string source, ulong operationId) =>
            Records.Enqueue((level, operationId));
    }
}
