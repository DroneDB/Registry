using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Push workflow interface (init, upload, save meta, commit).
/// </summary>
/// <summary>
/// A file streamed to the uploads folder of a dataset the current user can write to, with the
/// SHA-256 computed while it streamed.
/// </summary>
public sealed record PushUpload(IDDB Ddb, string TempPath, long Bytes, FileHashHint Hash);
