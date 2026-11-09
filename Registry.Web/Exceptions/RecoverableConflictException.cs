using System;

namespace Registry.Web.Exceptions;

/// <summary>
/// A 409 that the same request can still get past: the client waits for an operation to end, or sends what is missing
/// first, then sends it again (a chunked upload with missing chunks, an upload that is still being received). The
/// upload answers keep <c>noRetry</c> false for it, where every other 409 is final (<see cref="Utilities.UploadRetryRule"/>).
/// </summary>
public class RecoverableConflictException : ConflictException
{
    public RecoverableConflictException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
