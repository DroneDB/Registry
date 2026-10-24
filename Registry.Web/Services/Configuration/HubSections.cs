using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Utilities;
using static Registry.Web.Services.Configuration.ConfigurationFields;

namespace Registry.Web.Services.Configuration;

/// <summary>The admin configuration sections of what the Hub shows: its options, the organization management, the thumbnails.</summary>
public sealed class HubSections : IConfigurationSectionBuilder
{
    private readonly IConfigurationView _view;

    public HubSections(IConfigurationView view)
    {
        _view = view;
    }

    public IEnumerable<PlacedSection> Build() =>
    [
        new(ConfigurationSection.HubOptions, BuildHubOptionsSection()),
        new(ConfigurationSection.OrgManagement, BuildOrgManagementSection()),
        new(ConfigurationSection.Thumbnails, BuildThumbnailsSection())
    ];

    private ConfigurationSectionDataDto BuildHubOptionsSection()
    {
        var hub = _view.ConfiguredOrNull<HubOptions>();
        var hubDefault = _view.ShippedOrNull<HubOptions>();
        var ho = hub;
        var do_ = hubDefault;
        return Section("HubOptions", "Hub Options",
            "UI branding, white-label customization, and feature visibility toggles.",
            [
                StringField("AppLogo", "App Logo",
                    "URL of the navbar logo. Use /branding/... for user-supplied assets.",
                    ho?.AppLogo, do_?.AppLogo),

                StringField("AppName", "App Name",
                    "Display name used as document title. Defaults to 'DroneDB' when null.",
                    ho?.AppName, do_?.AppName),

                StringField("AppIcon", "App Icon",
                    "CSS icon class or URL shown next to app name. Defaults to 'icon-dronedb'.",
                    ho?.AppIcon, do_?.AppIcon),

                BoolField("ShowRegistrationLink", "Show Registration Link",
                    "Whether to show the registration link on the login page.",
                    ho?.ShowRegistrationLink ?? true, do_?.ShowRegistrationLink ?? true),

                BoolField("DisableDatasetCreation", "Disable Dataset Creation",
                    "Hides the 'New dataset' button and all dataset creation entry-points.",
                    ho?.DisableDatasetCreation ?? false, do_?.DisableDatasetCreation ?? false),

                BoolField("DisableStorageInfo", "Disable Storage Info",
                    "Hides the per-user storage indicator in the header.",
                    ho?.DisableStorageInfo ?? false, do_?.DisableStorageInfo ?? false),

                BoolField("DisableAccountManagement", "Disable Account Management",
                    "Hides account-management menu items. Useful with external auth.",
                    ho?.DisableAccountManagement ?? false, do_?.DisableAccountManagement ?? false),

                StringField("SingleOrganization", "Single Organization",
                    "When set, routes users directly to this organization slug (single-tenant mode).",
                    ho?.SingleOrganization, do_?.SingleOrganization),

                BoolField("ReadOnlyOrgs", "Read-Only Organizations",
                    "Hides organization create/delete/edit actions.",
                    ho?.ReadOnlyOrgs ?? false, do_?.ReadOnlyOrgs ?? false),

                JsonField("Favicon", "Favicon / Web Manifest",
                    "Favicon and web manifest configuration: FaviconIco, Favicon16, Favicon32, AppleTouchIcon, Manifest, ThemeColor. Files should be placed under {StoragePath}/branding/.",
                    ho?.Favicon, do_?.Favicon)
            ]);
    }

    private ConfigurationSectionDataDto BuildOrgManagementSection()
    {
        var account = _view.Configured<AccountOptions>();
        var accountDefault = _view.Shipped<AccountOptions>();
        return Section("OrgManagement", "Organization Management",
            "Controls for organization member management and default user organizations.",
            [
                BoolField("EnableOrganizationMemberManagement", "Enable Member Management",
                    "When enabled, organization owners can manage members. When disabled, only system admins can.",
                    account.EnableOrganizationMemberManagement, accountDefault.EnableOrganizationMemberManagement),

                BoolField("EnableDefaultUserOrganization", "Enable Default User Organization",
                    "When enabled, new users automatically get a personal default organization.",
                    account.EnableDefaultUserOrganization, accountDefault.EnableDefaultUserOrganization)
            ]);
    }

    private ConfigurationSectionDataDto BuildThumbnailsSection()
    {
        var host = _view.Configured<HostResourceOptions>().WithDefaults();
        var hostDefault = _view.Shipped<HostResourceOptions>().WithDefaults();
        var thumbnails = _view.Configured<ThumbnailOptions>();
        var thumbnailsDefault = _view.Shipped<ThumbnailOptions>();
        return Section("Thumbnails", "Thumbnails",
            "Remote thumbnail generator URL, default size, and candidate file names.",
            [
                StringField("RemoteThumbnailGeneratorUrl", "Remote Thumbnail Generator URL",
                    "URL of a remote thumbnail generator service. Null uses the local generator.",
                    thumbnails.RemoteThumbnailGeneratorUrl, thumbnailsDefault.RemoteThumbnailGeneratorUrl),

                SensitiveField("ThumbnailGeneratorToken", "Thumbnail Generator Token",
                    "Shared secret between the web server and the ThumbnailGenerator host. Generated on first start when empty.",
                    thumbnails.ThumbnailGeneratorToken, thumbnailsDefault.ThumbnailGeneratorToken),

                NumberField("DefaultThumbnailSize", "Default Thumbnail Size",
                    "Default thumbnail size in pixels when no size is specified.",
                    thumbnails.DefaultThumbnailSize, thumbnailsDefault.DefaultThumbnailSize,
                    unit: "pixels"),

                ArrayField("DatasetThumbnailCandidates", "Dataset Thumbnail Candidates",
                    "File names (in dataset root) considered as dataset thumbnail candidates, in priority order.",
                    thumbnails.Candidates, thumbnailsDefault.Candidates),

                NumberField("NativeConcurrency.Tiles", "Concurrent Tile Renders",
                    "Map tiles and map images rendered at once by this instance. 0 = half the CPU cores (at least 2).",
                    host.NativeConcurrency.Tiles, hostDefault.NativeConcurrency.Tiles, minValue: 0),

                NumberField("NativeConcurrency.Heavy", "Concurrent Thumbnail Renders",
                    "Thumbnails and coverages rendered at once by this instance. 0 = a quarter of the CPU cores (at least 1).",
                    host.NativeConcurrency.Heavy, hostDefault.NativeConcurrency.Heavy, minValue: 0)
            ]);
    }
}
