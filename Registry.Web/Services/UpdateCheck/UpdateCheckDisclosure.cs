namespace Registry.Web.Services.UpdateCheck;

/// <summary>
/// What an administrator is told about the update check: one wording, used by the start-up log, the "Update check"
/// section of the admin configuration view and the documentation, so they cannot drift apart.
/// </summary>
public static class UpdateCheckDisclosure
{
    /// <summary>The placeholder of <see cref="Template"/> that takes the instance id.</summary>
    public const string InstanceIdPlaceholder = "{instanceId}";

    /// <summary>The page that describes the update check and how to switch it off.</summary>
    public const string DocsUrl = "https://docs.dronedb.app/docs/registry/admin/update-check";

    /// <summary>What the log says when the check is switched off.</summary>
    public const string DisabledText = "Update check: disabled";

    /// <summary>The disclosure of an enabled check, with <see cref="InstanceIdPlaceholder"/> for the instance id.</summary>
    public const string Template =
        "Update check: enabled (daily). This deployment sends its instance ID ({instanceId}), Registry/DroneDB versions, platform, " +
        "public URL, host name, network domain and declared use to dronedb.app, and a contact email only if an admin opted in; " +
        "dronedb.app also records the source IP address and checks the public URL. Purposes: update notices, release notes and " +
        "license-compliance verification. Docs: docs.dronedb.app/docs/registry/admin/update-check  ·  Opt out: UpdateCheckCron=\"disabled\"";

    /// <summary>The disclosure for the deployment <paramref name="instanceId"/>.</summary>
    public static string For(string instanceId) => Template.Replace(InstanceIdPlaceholder, instanceId, System.StringComparison.Ordinal);
}
