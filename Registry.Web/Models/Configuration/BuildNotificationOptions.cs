#nullable enable
namespace Registry.Web.Models.Configuration;

/// <summary>The build notification e-mails to users (the <c>AppSettings:BuildNotifications</c> section).</summary>
public sealed class BuildNotificationOptions
{
    /// <summary>The default of <see cref="DigestMinutes"/>.</summary>
    public const int DefaultDigestMinutes = 5;

    /// <summary>The shortest <see cref="DigestMinutes"/>.</summary>
    public const int MinDigestMinutes = 1;

    /// <summary>The longest <see cref="DigestMinutes"/>.</summary>
    public const int MaxDigestMinutes = 60;

    /// <summary>
    /// How long the first finished build of a user waits for others: one e-mail then lists every build of that user
    /// that ended in the window, so an upload of many files does not send one e-mail per file.
    /// </summary>
    public int DigestMinutes { get; set; } = DefaultDigestMinutes;
}
