#nullable enable
using Microsoft.Extensions.Logging;
using Registry.Web.Exceptions;

namespace Registry.Web.Services.HeavyTasks.Ports;

/// <summary>
/// Thrown when a task submission is rejected by the quota guard. Carries the
/// HTTP-mappable <see cref="HeavyTaskQuotaCode"/> (413 or 429).
/// </summary>
public sealed class HeavyTaskQuotaException : RegistryException
{
    public HeavyTaskQuotaException(HeavyTaskQuotaCode code, string message) : base((int)code, message, noRetry: true)
    {
        Code = code;
    }

    public HeavyTaskQuotaCode Code { get; }

    /// <summary>Resource limits are logged at Warning, like the storage quota.</summary>
    public override LogLevel Level => LogLevel.Warning;
}

/// <summary>Thrown when a requested tool id/version is not registered (HTTP 400).</summary>
public sealed class HeavyToolNotFoundException : BadRequestException
{
    public HeavyToolNotFoundException(string message) : base(message) { }
}
