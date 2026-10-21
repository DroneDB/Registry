#nullable enable
using System.Collections.Generic;

namespace Registry.Web.Test.Support;

/// <summary>
/// RQ-09 / D4 fixtures: the exact Hangfire InvocationData JSON of the jobs Registry v2.7.1 stores,
/// captured at b7026d48 (before RQ-09) with the production serializer settings
/// (CompatibilityLevel 1.8, simple assembly names, recommended settings). Datasets live under
/// <see cref="CapturedRoot"/>; tests move it to a temporary folder.
/// </summary>
public static class LegacyJobPayloads
{
    /// <summary>The dataset root in the captured payloads.</summary>
    public const string CapturedRoot = "/tmp/rq09-capture";

    private static readonly string DdbArg =
        "{\\\"$type\\\":\\\"Registry.Adapters.DroneDB.DDB, Registry.Adapters\\\",\\\"DatasetFolderPath\\\":\\\"/tmp/rq09-capture/org1/3f2504e0-4f89-11d3-9a0c-0305e82c3301\\\",\\\"BuildFolderPath\\\":\\\"/tmp/rq09-capture/org1/3f2504e0-4f89-11d3-9a0c-0305e82c3301/.ddb/build\\\"}";

    private const string Utils = "Registry.Web.Utilities.HangfireUtils, Registry.Web";
    private const string Ctx = "Hangfire.Server.PerformContext, Hangfire.Core";

    /// <summary>The captured payloads (name → InvocationData JSON), verbatim.</summary>
    public static readonly IReadOnlyDictionary<string, string> Captured = new Dictionary<string, string>
    {
        ["BuildWrapper"] = "{\"t\":\"" + Utils + "\",\"m\":\"BuildWrapper\",\"p\":[\"Registry.Ports.DroneDB.IDDB, Registry.Ports\",\"System.String\",\"System.Boolean\",\"" + Ctx + "\"],\"a\":[\"" + DdbArg + "\",\"\\\"a/b.tif\\\"\",\"false\",null]}",
        ["BuildWrapperNullPath"] = "{\"t\":\"" + Utils + "\",\"m\":\"BuildWrapper\",\"p\":[\"Registry.Ports.DroneDB.IDDB, Registry.Ports\",\"System.String\",\"System.Boolean\",\"" + Ctx + "\"],\"a\":[\"" + DdbArg + "\",null,\"true\",null]}",
        ["BuildPendingWrapper"] = "{\"t\":\"" + Utils + "\",\"m\":\"BuildPendingWrapper\",\"p\":[\"Registry.Ports.DroneDB.IDDB, Registry.Ports\",\"" + Ctx + "\"],\"a\":[\"" + DdbArg + "\",null]}",
        ["CleanupWrapper"] = "{\"t\":\"" + Utils + "\",\"m\":\"CleanupWrapper\",\"p\":[\"Registry.Ports.DroneDB.IDDB, Registry.Ports\",\"" + Ctx + "\"],\"a\":[\"" + DdbArg + "\",null]}",
        ["GenerateThumbnailWrapper"] = "{\"t\":\"" + Utils + "\",\"m\":\"GenerateThumbnailWrapper\",\"p\":[\"Registry.Ports.DroneDB.IDDB, Registry.Ports\",\"System.String\",\"System.Int32\",\"System.String\",\"" + Ctx + "\"],\"a\":[\"" + DdbArg + "\",\"\\\"a.jpg\\\"\",\"256\",\"\\\"/tmp/t.jpg\\\"\",null]}",
        ["MaskBordersWrapper"] = "{\"t\":\"" + Utils + "\",\"m\":\"MaskBordersWrapper\",\"p\":[\"Registry.Ports.DroneDB.IDDB, Registry.Ports\",\"System.String\",\"System.String\",\"System.Int32\",\"System.Boolean\",\"" + Ctx + "\"],\"a\":[\"" + DdbArg + "\",\"\\\"a.tif\\\"\",\"\\\"a_masked.tif\\\"\",\"15\",\"true\",null]}",
        ["SafeDelete"] = "{\"t\":\"" + Utils + "\",\"m\":\"SafeDelete\",\"p\":[\"System.String\",\"" + Ctx + "\"],\"a\":[\"\\\"/tmp/x\\\"\",null]}",
        ["CleanupExpiredJobs"] = "{\"t\":\"" + Utils + "\",\"m\":\"CleanupExpiredJobs\",\"p\":[\"" + Ctx + "\"],\"a\":[null]}",
        ["DummyJob"] = "{\"t\":\"" + Utils + "\",\"m\":\"DummyJob\",\"p\":[\"" + Ctx + "\"],\"a\":[null]}",
        ["DeleteWorkDirJob"] = "{\"t\":\"Registry.Web.Services.HeavyTasks.Adapters.HeavyTaskJobWrapper, Registry.Web\",\"m\":\"DeleteWorkDirJob\",\"p\":[\"System.String\"],\"a\":[\"\\\"/tmp/w\\\"\"]}",
        ["HeavyRun"] = "{\"t\":\"Registry.Web.Services.HeavyTasks.Adapters.HeavyTaskJobWrapper, Registry.Web\",\"m\":\"Run\",\"p\":[\"System.String\",\"System.String\",\"System.String\",\"" + Ctx + "\",\"System.Threading.CancellationToken, mscorlib\"],\"a\":[\"\\\"build\\\"\",\"\\\"1\\\"\",\"\\\"{}\\\"\",null,null]}",
        ["CleanupDeletedDataset"] = "{\"t\":\"Registry.Web.Services.Adapters.DatasetCleanupService, Registry.Web\",\"m\":\"CleanupDeletedDatasetAsync\",\"p\":[\"System.String\",\"System.String\",\"System.Guid, mscorlib\",\"" + Ctx + "\"],\"a\":[\"\\\"org1\\\"\",\"\\\"ds1\\\"\",\"\\\"3f2504e0-4f89-11d3-9a0c-0305e82c3301\\\"\",null]}"
    };

}
