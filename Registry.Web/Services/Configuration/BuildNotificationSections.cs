using System.Collections.Generic;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using static Registry.Web.Services.Configuration.ConfigurationFields;

namespace Registry.Web.Services.Configuration;

/// <summary>The admin configuration section of the build notification e-mails (<c>AppSettings:BuildNotifications</c>).</summary>
public sealed class BuildNotificationSections : IConfigurationSectionBuilder
{
    private readonly ConfigurationView _view;

    public BuildNotificationSections(ConfigurationView view)
    {
        _view = view;
    }

    public IEnumerable<PlacedSection> Build() =>
    [
        new(ConfigurationSection.BuildNotifications, BuildSection())
    ];

    private ConfigurationSectionDataDto BuildSection()
    {
        var options = _view.ConfiguredOrNull<BuildNotificationOptions>();
        var shipped = _view.Shipped<BuildNotificationOptions>();

        // The section name is the key of the file (AppSettings:BuildNotifications): the editor writes the fields under it
        return Section("BuildNotifications", "Build notifications",
            "E-mails to the users who asked for them in their preferences (none by default, all, or only failed builds) when " +
            "their builds complete or fail for good. Needs the e-mail server (AppSettings:Smtp). The text is edited under " +
            "E-mail templates.",
            [
                NumberField("DigestMinutes", "Digest Window",
                    "How long the first finished build of a user waits for the others: one e-mail lists every build of that " +
                    "user that ended in the window.",
                    options?.DigestMinutes ?? shipped.DigestMinutes, shipped.DigestMinutes,
                    minValue: BuildNotificationOptions.MinDigestMinutes, maxValue: BuildNotificationOptions.MaxDigestMinutes,
                    unit: "minutes")
            ]);
    }
}
