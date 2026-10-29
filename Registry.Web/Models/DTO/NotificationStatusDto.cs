namespace Registry.Web.Models.DTO;

/// <summary>Whether the signed-in user can get notification e-mails (<c>GET /users/notifications/status</c>).</summary>
public sealed class NotificationStatusDto
{
    /// <summary>The server does not send e-mail (<c>AppSettings:Smtp:Enabled</c> is off).</summary>
    public const string EmailDisabled = "emailDisabled";

    /// <summary>The account has no valid e-mail address.</summary>
    public const string NoAddress = "noAddress";

    /// <summary>Whether an e-mail to the user can be sent.</summary>
    public bool CanReceiveEmail { get; set; }

    /// <summary>Why not: <see cref="EmailDisabled"/> or <see cref="NoAddress"/>; null when it can.</summary>
    public string? Reason { get; set; }
}
