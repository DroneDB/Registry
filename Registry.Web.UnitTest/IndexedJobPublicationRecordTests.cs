#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.States;
using NUnit.Framework;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>Publication record (de)serialization/validation and the Indexing state handler's set scores.</summary>
[TestFixture]
[NonParallelizable]
public class IndexedJobPublicationRecordTests
{
    private static readonly DateTime Created = new(2026, 5, 6, 7, 8, 9, 123, DateTimeKind.Utc);

    private static IndexPayload FullPayload() =>
        new("org", "ds", "hash", "user", "pq", "a/b.obj", "tool-x", "3", "req-hash", "parent", "wf");

    private static IndexedJobPublicationRecord Immediate() =>
        IndexedJobPublicationRecord.ForImmediate(FullPayload(), Created, "critical");

    // ---------------------------------------------------------------- round trip

    [Test]
    public void Immediate_RoundTrip_PreservesEveryField()
    {
        var record = Immediate();
        record.Validate().ShouldBeNull();

        var parsed = IndexedJobPublicationRecord.TryParse(record.ToStateData());

        parsed.Status.ShouldBe(IndexedJobRecordParseStatus.Valid);
        var r = parsed.Record!;
        r.SchemaVersion.ShouldBe(IndexedJobPublicationRecord.CurrentSchemaVersion);
        r.CreatedAtUtc.ShouldBe(Created);
        r.CreatedAtUtc.Kind.ShouldBe(DateTimeKind.Utc);
        r.Destination.ShouldBe(IndexedJobDestination.Immediate);
        r.Queue.ShouldBe("critical");
        r.EnqueueAtUtc.ShouldBeNull();
        r.Payload.ShouldBe(FullPayload());
        r.CreateDestinationState().ShouldBeOfType<EnqueuedState>().Queue.ShouldBe("critical");
    }

    [Test]
    public void Scheduled_RoundTrip_KeepsAbsoluteTime()
    {
        var record = IndexedJobPublicationRecord.ForScheduled(FullPayload(), Created, TimeSpan.FromMinutes(90));
        record.EnqueueAtUtc.ShouldBe(Created.AddMinutes(90));

        var parsed = IndexedJobPublicationRecord.TryParse(record.ToStateData());

        parsed.Status.ShouldBe(IndexedJobRecordParseStatus.Valid);
        parsed.Record!.Destination.ShouldBe(IndexedJobDestination.Scheduled);
        parsed.Record.EnqueueAtUtc.ShouldBe(Created.AddMinutes(90));
        parsed.Record.EnqueueAtUtc!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        parsed.Record.Queue.ShouldBeNull();
        parsed.Record.CreateDestinationState().ShouldBeOfType<ScheduledState>()
            .EnqueueAt.ShouldBe(Created.AddMinutes(90));
    }

    [Test]
    public void ScheduledInThePast_IsStillValid_AndKeptAsPastDue()
    {
        var record = IndexedJobPublicationRecord.ForScheduled(FullPayload(), Created, TimeSpan.FromSeconds(-30));
        IndexedJobPublicationRecord.TryParse(record.ToStateData()).Status.ShouldBe(IndexedJobRecordParseStatus.Valid);
    }

    [Test]
    public void CreationTime_IsTruncatedToMilliseconds()
    {
        var withTicks = Created.AddTicks(4567);
        IndexedJobPublicationRecord.ForImmediate(FullPayload(), withTicks, "default").CreatedAtUtc.ShouldBe(Created);
        IndexedJobPublicationRecord.TruncateToMilliseconds(DateTime.SpecifyKind(withTicks, DateTimeKind.Local))
            .Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public void BlankToolIdentity_IsNormalizedToIndexDefaults()
    {
        var payload = new IndexPayload("o", "d", null, null, ToolId: " ", ToolVersion: "");
        var record = IndexedJobPublicationRecord.ForImmediate(payload, Created, "default");

        record.Payload.ToolId.ShouldBe("build");
        record.Payload.ToolVersion.ShouldBe("1");
        record.Validate().ShouldBeNull();
        IndexedJobPublicationRecord.TryParse(record.ToStateData()).Status.ShouldBe(IndexedJobRecordParseStatus.Valid);
    }

    [Test]
    public void NullOptionalPayloadFields_RoundTripAsNull()
    {
        var record = IndexedJobPublicationRecord.ForImmediate(new IndexPayload("o", "d", null, null), Created, "default");
        var r = IndexedJobPublicationRecord.TryParse(record.ToStateData()).Record!;
        r.Payload.Hash.ShouldBeNull();
        r.Payload.Path.ShouldBeNull();
        r.Payload.RequestHash.ShouldBeNull();
        r.Payload.ParentJobId.ShouldBeNull();
        r.Payload.WorkflowExecutionId.ShouldBeNull();
    }

    [Test]
    public void IndexingState_SerializesTheRecord_AndIsNotFinal()
    {
        var state = new IndexingState(Immediate());
        state.Name.ShouldBe("Indexing");
        state.IsFinal.ShouldBeFalse();
        state.SerializeData().Keys.ShouldContain(IndexedJobPublicationRecord.SchemaVersionKey);
        state.SerializeData().Keys.ShouldContain(IndexedJobPublicationRecord.RecordKey);
        IndexedJobPublicationRecord.TryParse(state.SerializeData()).Status.ShouldBe(IndexedJobRecordParseStatus.Valid);
        Should.Throw<ArgumentNullException>(() => new IndexingState(null!));
    }

    // ---------------------------------------------------------------- invalid / newer

    private static Dictionary<string, string> Data(string json, string version = "1") => new()
    {
        [IndexedJobPublicationRecord.SchemaVersionKey] = version,
        [IndexedJobPublicationRecord.RecordKey] = json
    };

    private static string Json(string? destination = "Immediate", string? queue = "\"q\"", string? enqueueAt = "null",
        string payload = "{\"orgSlug\":\"o\",\"dsSlug\":\"d\",\"toolId\":\"build\",\"toolVersion\":\"1\"}",
        string version = "1", string created = "\"2026-05-06T07:08:09.123Z\"") =>
        "{\"schemaVersion\":" + version + ",\"createdAtUtc\":" + created + ",\"destination\":" +
        (destination == null ? "null" : "\"" + destination + "\"") + ",\"queue\":" + queue +
        ",\"enqueueAtUtc\":" + enqueueAt + ",\"payload\":" + payload + "}";

    [Test]
    public void ReferenceJson_IsValid() =>
        IndexedJobPublicationRecord.TryParse(Data(Json())).Status.ShouldBe(IndexedJobRecordParseStatus.Valid);

    [Test]
    public void UnknownMembers_AreTolerated() =>
        IndexedJobPublicationRecord.TryParse(Data(Json().Replace("\"queue\"", "\"extra\":{\"a\":[1]},\"queue\"")))
            .Status.ShouldBe(IndexedJobRecordParseStatus.Valid);

    private static IEnumerable<TestCaseData> InvalidCases()
    {
        yield return new TestCaseData(null).SetName("null state data");
        yield return new TestCaseData(new Dictionary<string, string>()).SetName("empty state data");
        yield return new TestCaseData(new Dictionary<string, string>
            { [IndexedJobPublicationRecord.SchemaVersionKey] = "1" }).SetName("record key missing");
        yield return new TestCaseData(Data("   ")).SetName("blank record");
        yield return new TestCaseData(Data("{ not json")).SetName("not json");
        yield return new TestCaseData(Data("[]")).SetName("json array");
        yield return new TestCaseData(Data("{}")).SetName("no inner schema version");
        yield return new TestCaseData(Data(Json(), "abc")).SetName("outer version not a number");
        yield return new TestCaseData(Data(Json(), "0")).SetName("outer version zero");
        yield return new TestCaseData(Data(Json(version: "0"), "0")).SetName("version zero");
        yield return new TestCaseData(new Dictionary<string, string>
            { [IndexedJobPublicationRecord.RecordKey] = Json() }).SetName("outer version missing");
        yield return new TestCaseData(Data(Json(destination: "Continuation"))).SetName("unsupported destination");
        yield return new TestCaseData(Data(Json(destination: null))).SetName("null destination");
        yield return new TestCaseData(Data(Json(payload: "null"))).SetName("null payload");
        yield return new TestCaseData(Data(Json(payload: "{\"dsSlug\":\"d\",\"toolId\":\"build\"}"))).SetName("org missing");
        yield return new TestCaseData(Data(Json(payload: "{\"orgSlug\":\"o\",\"toolId\":\"build\"}"))).SetName("dataset missing");
        yield return new TestCaseData(Data(Json(payload: "{\"orgSlug\":\"o\",\"dsSlug\":\"d\"}"))).SetName("tool missing");
        yield return new TestCaseData(Data(Json(queue: "null"))).SetName("immediate without queue");
        yield return new TestCaseData(Data(Json(queue: "\"Not A Queue!\""))).SetName("queue rejected by Hangfire");
        yield return new TestCaseData(Data(Json(enqueueAt: "\"2026-05-06T07:08:09Z\""))).SetName("immediate with schedule time");
        yield return new TestCaseData(Data(Json(destination: "Scheduled", queue: "null", enqueueAt: "null")))
            .SetName("scheduled without time");
        yield return new TestCaseData(Data(Json(destination: "Scheduled", queue: "\"q\"",
            enqueueAt: "\"2026-05-06T07:08:09Z\""))).SetName("scheduled with queue");
        yield return new TestCaseData(Data(Json(created: "\"0001-01-01T00:00:00Z\""))).SetName("default creation time");
        yield return new TestCaseData(Data(Json(created: "\"garbage\""))).SetName("unparseable creation time");
    }

    [TestCaseSource(nameof(InvalidCases))]
    public void MalformedRecord_IsReportedInvalid_NeverThrows(Dictionary<string, string>? data)
    {
        IndexedJobRecordParseResult result = null!;
        Should.NotThrow(() => result = IndexedJobPublicationRecord.TryParse(data));

        result.Status.ShouldBe(IndexedJobRecordParseStatus.Invalid);
        result.Record.ShouldBeNull();
        result.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void OuterVersionNewer_IsNewerSchema_EvenIfRecordIsUnreadable()
    {
        var result = IndexedJobPublicationRecord.TryParse(Data("<<binary garbage>>", "2"));
        result.Status.ShouldBe(IndexedJobRecordParseStatus.NewerSchema);
        result.Record.ShouldBeNull();
    }

    [Test]
    public void InnerVersionNewer_IsNewerSchema() =>
        IndexedJobPublicationRecord.TryParse(Data(Json(version: "7"), "1")).Status
            .ShouldBe(IndexedJobRecordParseStatus.NewerSchema);

    [Test]
    public void OuterAndInnerVersionMismatch_IsInvalid() =>
        IndexedJobPublicationRecord.TryParse(Data(Json(version: "1"), "0")).Status
            .ShouldBe(IndexedJobRecordParseStatus.Invalid);

    // ---------------------------------------------------------------- handler scores

    [Test]
    public void Scores_AreWholeMinutesSinceEpoch_RoundedUpForDueAndDownForNow()
    {
        var epoch = IndexingStateHandler.ScoreEpochUtc;
        IndexingStateHandler.ToScore(epoch).ShouldBe(0);
        IndexingStateHandler.ToScore(epoch.AddMinutes(10)).ShouldBe(10);
        IndexingStateHandler.ToScore(epoch.AddMinutes(10).AddSeconds(1)).ShouldBe(11);
        IndexingStateHandler.ScoreForNow(epoch.AddMinutes(10).AddSeconds(59)).ShouldBe(10);
        IndexingStateHandler.ToScore(epoch.AddDays(-3)).ShouldBe(0, "before the epoch clamps to 0");
        IndexingStateHandler.ScoreForNow(epoch.AddDays(-3)).ShouldBe(0);
    }

    [Test]
    public void RescheduledScore_IsAlwaysAfterTheSamePassUpperBound()
    {
        var now = IndexingStateHandler.ScoreEpochUtc.AddMinutes(1000).AddSeconds(30);
        IndexingStateHandler.ToScore(now.AddMinutes(1)).ShouldBeGreaterThan(IndexingStateHandler.ScoreForNow(now));
    }

    [Test]
    public void Scores_AreExactInSinglePrecisionFloat()
    {
        // MySQL keeps set scores in a FLOAT column.
        var score = IndexingStateHandler.ToScore(IndexingStateHandler.ScoreEpochUtc.AddYears(20));
        ((double)(float)score).ShouldBe(score);
    }

    [Test]
    public void Scores_TreatLocalAndUnspecifiedKindsConsistently()
    {
        var utc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        IndexingStateHandler.ToScore(utc.ToLocalTime()).ShouldBe(IndexingStateHandler.ToScore(utc));
        IndexingStateHandler.ToScore(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified))
            .ShouldBe(IndexingStateHandler.ToScore(utc));
    }

    // ---------------------------------------------------------------- handler registration / atomicity

    [Test]
    public void Registration_IsIdempotent()
    {
        var saved = GlobalStateHandlers.Handlers.ToArray();
        try
        {
            GlobalStateHandlers.Handlers.Clear();
            IndexingStateHandler.IsRegistered().ShouldBeFalse();
            IndexingStateHandler.EnsureRegistered();
            IndexingStateHandler.EnsureRegistered();
            IndexingStateHandler.IsRegistered().ShouldBeTrue();
            GlobalStateHandlers.Handlers.OfType<IndexingStateHandler>().Count().ShouldBe(1);
            new IndexingStateHandler().StateName.ShouldBe("Indexing");
        }
        finally
        {
            GlobalStateHandlers.Handlers.Clear();
            foreach (var h in saved) GlobalStateHandlers.Handlers.Add(h);
        }
    }

    [Test]
    public void IndexingState_RegistersMemberAtCreationScore_AndLeavingRemovesIt()
    {
        var saved = GlobalStateHandlers.Handlers.ToArray();
        try
        {
            IndexingStateHandler.EnsureRegistered();
            var storage = new InMemoryStorage();
            var client = new BackgroundJobClient(storage);
            var record = IndexedJobPublicationRecord.ForImmediate(FullPayload(), DateTime.UtcNow.AddMinutes(-5), "default");

            var id = client.Create(PublicationEnv.NewJob(), new IndexingState(record));

            using (var c = storage.GetConnection())
            {
                var score = IndexingStateHandler.ToScore(record.CreatedAtUtc + IndexingStateHandler.GracePeriod);
                c.GetFirstByLowestScoreFromSet(IndexingStateHandler.RecoverySetKey, score, score).ShouldBe(id);
                c.GetFirstByLowestScoreFromSet(IndexingStateHandler.RecoverySetKey, 0, score - 1).ShouldBeNull();
            }

            client.ChangeState(id, new EnqueuedState(), "Indexing").ShouldBeTrue();
            using (var c = storage.GetConnection())
                c.GetAllItemsFromSet(IndexingStateHandler.RecoverySetKey).ShouldBeEmpty();
        }
        finally
        {
            GlobalStateHandlers.Handlers.Clear();
            foreach (var h in saved) GlobalStateHandlers.Handlers.Add(h);
        }
    }

    [Test]
    public void IndexingStateWithUnusableRecord_IsDueImmediately()
    {
        var saved = GlobalStateHandlers.Handlers.ToArray();
        try
        {
            IndexingStateHandler.EnsureRegistered();
            var storage = new InMemoryStorage();
            var client = new BackgroundJobClient(storage);

            var id = client.Create(PublicationEnv.NewJob(), new RawIndexingState(new Dictionary<string, string>()));

            using var c = storage.GetConnection();
            c.GetFirstByLowestScoreFromSet(IndexingStateHandler.RecoverySetKey, 0,
                IndexingStateHandler.ScoreForNow(DateTime.UtcNow.AddMinutes(1))).ShouldBe(id);
        }
        finally
        {
            GlobalStateHandlers.Handlers.Clear();
            foreach (var h in saved) GlobalStateHandlers.Handlers.Add(h);
        }
    }

    [Test]
    public void RecoverySetKey_IsDedicated()
    {
        IndexingStateHandler.RecoverySetKey.ShouldNotBe("schedule");
        IndexingStateHandler.GracePeriod.ShouldBe(TimeSpan.FromMinutes(2));
    }
}
