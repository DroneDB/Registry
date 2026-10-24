#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>Where an upload identified by its <c>X-Upload-Id</c> is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UploadReceiptState
{
    /// <summary>The body is being received.</summary>
    Receiving,

    /// <summary>The body is on disk; the file is being added to the dataset.</summary>
    Committing,

    /// <summary>The upload is in the dataset.</summary>
    Done,

    /// <summary>The upload was refused or failed; sending it again will not help unless the cause changes.</summary>
    Failed
}

/// <summary>The outcome of an upload, kept so a client that lost the response can ask for it.</summary>
/// <param name="State">Where the upload is.</param>
/// <param name="Results">The per-file results once <see cref="UploadReceiptState.Done"/> (one for a single-file upload).</param>
/// <param name="Status">HTTP status of the failure when <see cref="UploadReceiptState.Failed"/>.</param>
/// <param name="Error">Message of the failure.</param>
/// <param name="UpdatedUtc">Last change.</param>
public sealed record UploadReceipt(UploadReceiptState State, IReadOnlyList<UploadPartResultDto>? Results,
    int? Status, string? Error, DateTime UpdatedUtc);

/// <summary>
/// Keeps the outcome of uploads by client-generated id (UPL-01 E.4): when a proxy times out after
/// the last byte (504) while the Registry is still adding the file, the client asks here instead
/// of uploading the whole file again.
/// </summary>
public interface IUploadReceiptStore
{
    Task<UploadReceipt?> GetAsync(string orgSlug, string dsSlug, string uploadId);
    Task SetAsync(string orgSlug, string dsSlug, string uploadId, UploadReceipt receipt);
    Task RemoveAsync(string orgSlug, string dsSlug, string uploadId);
}
