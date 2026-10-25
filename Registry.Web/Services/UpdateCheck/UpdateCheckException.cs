#nullable enable
using System;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>An update check that failed: it asked in vain, or the answer was not usable. Never shown to a user.</summary>
public sealed class UpdateCheckException : Exception
{
    public UpdateCheckException(string message) : base(message)
    {
    }

    public UpdateCheckException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
