using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Registry.Common.Model;

namespace Registry.Ports.DroneDB;

/// <summary>
/// Full DroneDB dataset API. Decomposed into role interfaces (<see cref="IDdbIndex"/>,
/// <see cref="IDdbBuild"/>, <see cref="IDdbMeta"/>, <see cref="IDdbRaster"/>,
/// <see cref="IDdbAnalytics"/>) per the Interface Segregation Principle so narrow consumers can
/// depend on just the role they need instead of this 50+ member aggregate (ImproveParallelWrites
/// plan, workstream 04 §7). <c>DDB</c> (Registry.Adapters) implements this unchanged - no
/// consumer of <see cref="IDDB"/> itself breaks.
/// </summary>
public interface IDDB : IDdbIndex, IDdbBuild, IDdbMeta, IDdbRaster, IDdbAnalytics
{
    // These consts are like magic strings: if anything changes this goes kaboom!
    public const string DatabaseFolderName = ".ddb";
    public const string BuildFolderName = "build";
    public const string TmpFolderName = "tmp";
}
