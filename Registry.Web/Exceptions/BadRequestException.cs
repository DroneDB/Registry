#nullable enable
using System;
using Microsoft.AspNetCore.Http;

namespace Registry.Web.Exceptions;

/// <summary>The request is invalid (HTTP 400): a missing or malformed argument, a forbidden value.</summary>
public class BadRequestException : RegistryException
{
    public BadRequestException(string message, bool noRetry = false, Exception? innerException = null)
        : base(StatusCodes.Status400BadRequest, message, noRetry, innerException)
    {
    }
}
