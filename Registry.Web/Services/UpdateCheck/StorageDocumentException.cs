#nullable enable
using System.IO;
using System;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>
/// A <see cref="StorageDocument{T}"/> that could not be written: the disk refused it (an I/O error, or no permission on
/// the storage folder). One type for the callers that treat a lost document as a failure of their own work and go on.
/// </summary>
public sealed class StorageDocumentException : IOException
{
    public StorageDocumentException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
