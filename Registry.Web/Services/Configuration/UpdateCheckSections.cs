#nullable enable
using System.Collections.Generic;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Services.UpdateCheck;
using static Registry.Web.Services.Configuration.ConfigurationFields;

namespace Registry.Web.Services.Configuration;

/// <summary>
/// The admin configuration section of the update check. Its description is the disclosure of what the check sends, with
/// the instance id and the documentation page: the administrator always sees it here, whatever the log level.
/// </summary>
public sealed class UpdateCheckSections : IConfigurationSectionBuilder
{
    private const string EmailOffNote = " E-mail is off (AppSettings:Smtp is not enabled): no update e-mail is sent, whatever this is set to.";

    private readonly ConfigurationView _view;
    private readonly IEmailService _email;
    private readonly IInstanceIdentity _identity;

    public UpdateCheckSections(ConfigurationView view, IEmailService email, IInstanceIdentity identity)
    {
        _view = view;
        _email = email;
        _identity = identity;
    }

    public IEnumerable<PlacedSection> Build() =>
    [
        new(ConfigurationSection.UpdateCheck, BuildUpdateCheckSection())
    ];

    private ConfigurationSectionDataDto BuildUpdateCheckSection()
    {
        var options = _view.Configured<UpdateCheckOptions>();
        var shipped = _view.Shipped<UpdateCheckOptions>();
        var enabled = !CronSetting.IsDisabled(options.UpdateCheckCron);
        var instanceId = _identity.Current.Id;

        return Section("UpdateCheck", "Update check",
            enabled ? UpdateCheckDisclosure.For(instanceId)
                : $"{UpdateCheckDisclosure.DisabledText} (AppSettings:UpdateCheckCron). Docs: {UpdateCheckDisclosure.DocsUrl}",
            [
                StringField("UpdateCheckCron", "Update Check Cron",
                    "Cron expression of the daily update check. Empty: once a day at a time that depends on the instance. Set to 'disabled' to switch the check off: nothing is sent.",
                    options.UpdateCheckCron!, shipped.UpdateCheckCron!, "cron"),

                EnumField("UpdateCheckEmail", "Update E-mail",
                    "Which updates are e-mailed to the administrators: 'security' (only updates with security fixes), 'all' or 'off'." +
                    (_email.IsEnabled ? string.Empty : EmailOffNote),
                    options.UpdateCheckEmail, shipped.UpdateCheckEmail, ["security", "all", "off"]),

                // Not a setting: created at the first start and kept in the storage folder
                InfoField("InstanceId", "Instance ID",
                    "The identity of this deployment, to quote in a request to delete the data held about it.",
                    instanceId)
            ]);
    }
}
