#nullable enable
using System;
using Microsoft.AspNetCore.Http;

namespace Registry.Web.Exceptions;

/// <summary>The resource does not exist (HTTP 404).</summary>
public class NotFoundException : RegistryException
{
    public NotFoundException(string message) : base(StatusCodes.Status404NotFound, message)
    {
    }

    public NotFoundException(string message, Exception inner)
        : base(StatusCodes.Status404NotFound, message, innerException: inner)
    {
    }
}
