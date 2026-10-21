#nullable enable
using System;

namespace Registry.Web.Models.Configuration;

/// <summary>Share batches and generated dataset names (keys of the <c>AppSettings</c> section).</summary>
public sealed class ShareOptions
{
    /// <summary>Length of batch tokens (at least 8).</summary>
    public int BatchTokenLength { get; set; }

    /// <summary>Timeout of batch uploads (at least 1 minute).</summary>
    public TimeSpan UploadBatchTimeout { get; set; }

    /// <summary>Length of the random generated dataset name (at least 8).</summary>
    public int RandomDatasetNameLength { get; set; }
}
