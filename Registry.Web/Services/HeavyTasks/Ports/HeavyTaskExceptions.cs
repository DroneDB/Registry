#nullable enable
using System;

namespace Registry.Web.Services.HeavyTasks.Ports;

/// <summary>
/// Thrown when a task submission is rejected by the quota guard. Carries the
/// HTTP-mappable <see cref="HeavyTaskQuotaCode"/> (413 or 429).
/// </summary>
public sealed class HeavyTaskQuotaException : Exception
{
    public HeavyTaskQuotaException(HeavyTaskQuotaCode code, string message) : base(message)
    {
        Code = code;
    }

    public HeavyTaskQuotaCode Code { get; }
}

/// <summary>Thrown when a requested tool id/version is not registered.</summary>
public sealed class HeavyToolNotFoundException : Exception
{
    public HeavyToolNotFoundException(string message) : base(message) { }
}
