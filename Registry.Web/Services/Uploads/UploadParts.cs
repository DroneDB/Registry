#nullable enable
using System;
using Microsoft.AspNetCore.Http;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Uploads;

/// <summary>The result of one file of an upload request: the entry it became or the error that stopped it.</summary>
public sealed record UploadPartOutcome(string Path, EntryDto? Entry, Exception? Error)
{
    public UploadPartResultDto ToDto(bool authenticated)
    {
        if (Error == null)
            return new UploadPartResultDto { Path = Path, Status = StatusCodes.Status201Created, Entry = Entry };

        var d = ApiExceptionClassifier.Classify(Error, authenticated);
        return new UploadPartResultDto
        {
            Path = Path,
            Status = d.StatusCode,
            Error = d.Message,
            Retryable = !d.NoRetry && d.StatusCode is StatusCodes.Status503ServiceUnavailable
                or StatusCodes.Status429TooManyRequests
        };
    }
}

/// <summary>A file moved into the dataset and waiting for (or past) its index step.</summary>
public sealed class MovedUploadPart(StreamedPart part, string localPath)
{
    public StreamedPart Part { get; } = part;
    public string LocalPath { get; } = localPath;
    public Entry? Entry { get; set; }
    public Exception? Error { get; set; }
}
