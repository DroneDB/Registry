#nullable enable
using System;
using Microsoft.AspNetCore.Http;

namespace Registry.Web.Exceptions;

/// <summary>
/// The request clashes with the current state of the resource (HTTP 409): it already exists, an
/// operation on it is in progress, it changed since the client read it.
/// </summary>
public class ConflictException : RegistryException
{
    public ConflictException(string message, Exception? innerException = null)
        : base(StatusCodes.Status409Conflict, message, innerException: innerException)
    {
    }
}
