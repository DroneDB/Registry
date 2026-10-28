#nullable disable
using System.Collections.Generic;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using static Registry.Web.Services.Configuration.ConfigurationFields;

namespace Registry.Web.Services.Configuration;

/// <summary>The admin configuration sections of the import of datasets.</summary>
public sealed class ImportSections : IConfigurationSectionBuilder
{
    private readonly ConfigurationView _view;

    public ImportSections(ConfigurationView view)
    {
        _view = view;
    }

    public IEnumerable<PlacedSection> Build() =>
    [
        new(ConfigurationSection.Import, BuildImportSection())
    ];

    private ConfigurationSectionDataDto BuildImportSection()
    {
        var import = _view.ConfiguredOrNull<ImportSettings>();
        var importDefault = _view.ShippedOrNull<ImportSettings>();
        var imp = import;
        var dImp = importDefault;
        var isActive = imp != null;

        var section = Section("Import", "Import Dataset",
            "Import settings: size limits, allowed source types, SSRF guard, timeouts, and concurrency.",
            [
                NumberField("MaxImportSizeBytes", "Max Import Size",
                    "Maximum total bytes that can be imported in a single task. 0 = unlimited.",
                    imp?.MaxImportSizeBytes ?? dImp?.MaxImportSizeBytes ?? 0,
                    dImp?.MaxImportSizeBytes ?? 0, unit: "bytes"),

                ArrayField("AllowedSourceTypes", "Allowed Source Types",
                    "Allowed import source types. Null/empty = all sources allowed. Values: 'registry', 'archive-url'.",
                    imp?.AllowedSourceTypes, dImp?.AllowedSourceTypes),

                BoolField("SsrfAllowPrivateNetworks", "SSRF: Allow Private Networks",
                    "Allow outbound connections to private/loopback/link-local addresses.",
                    imp?.SsrfAllowPrivateNetworks ?? dImp?.SsrfAllowPrivateNetworks ?? false,
                    dImp?.SsrfAllowPrivateNetworks ?? false),

                ArrayField("SsrfAllowedHosts", "SSRF: Allowed Hosts",
                    "Hostnames explicitly exempt from SSRF blocking.",
                    imp?.SsrfAllowedHosts, dImp?.SsrfAllowedHosts),

                NumberField("MaxRedirects", "Max Redirects",
                    "Maximum HTTP redirects to follow during import.",
                    imp?.MaxRedirects ?? dImp?.MaxRedirects ?? 0,
                    dImp?.MaxRedirects ?? 0),

                NumberField("ConnectTimeoutSeconds", "Connect Timeout",
                    "Connection/authentication timeout per source.",
                    imp?.ConnectTimeoutSeconds ?? dImp?.ConnectTimeoutSeconds ?? 0,
                    dImp?.ConnectTimeoutSeconds ?? 0, unit: "seconds"),

                NumberField("TransferTimeoutSeconds", "Transfer Timeout",
                    "Total transfer timeout per import task.",
                    imp?.TransferTimeoutSeconds ?? dImp?.TransferTimeoutSeconds ?? 0,
                    dImp?.TransferTimeoutSeconds ?? 0, unit: "seconds"),

                NumberField("RegistryDownloadConcurrency", "Registry Download Concurrency",
                    "Number of files downloaded in parallel from a remote registry.",
                    imp?.RegistryDownloadConcurrency ?? dImp?.RegistryDownloadConcurrency ?? 0,
                    dImp?.RegistryDownloadConcurrency ?? 0),

                NumberField("RegistryDownloadMaxRetries", "Registry Download Max Retries",
                    "Maximum retry attempts per file download against a remote registry.",
                    imp?.RegistryDownloadMaxRetries ?? dImp?.RegistryDownloadMaxRetries ?? 0,
                    dImp?.RegistryDownloadMaxRetries ?? 0),

                NumberField("MaxFileImportSizeBytes", "Max Single-File Import Size",
                    "Maximum size of a single file imported from URL (0 = fall back to MaxImportSizeBytes).",
                    imp?.MaxFileImportSizeBytes ?? dImp?.MaxFileImportSizeBytes ?? 0,
                    dImp?.MaxFileImportSizeBytes ?? 0, unit: "bytes"),

                NumberField("MinDownloadSpeedBytesPerSec", "Min Download Speed",
                    "Minimum sustained download speed before import is aborted (0 = disable).",
                    imp?.MinDownloadSpeedBytesPerSec ?? dImp?.MinDownloadSpeedBytesPerSec ?? 0,
                    dImp?.MinDownloadSpeedBytesPerSec ?? 0, unit: "bytes/s"),

                NumberField("LowSpeedGraceSeconds", "Low-Speed Grace Period",
                    "Time window for low-speed detection and hard stall timeout.",
                    imp?.LowSpeedGraceSeconds ?? dImp?.LowSpeedGraceSeconds ?? 0,
                    dImp?.LowSpeedGraceSeconds ?? 0, unit: "seconds"),

                ArrayField("BlockedFileExtensions", "Blocked File Extensions",
                    "File extensions rejected by all ingestion paths (block-list). Ignored when AllowedFileExtensions is non-empty.",
                    imp?.EffectiveBlockedFileExtensions(), dImp?.EffectiveBlockedFileExtensions()),

                ArrayField("AllowedFileExtensions", "Allowed File Extensions",
                    "File extensions accepted by all ingestion paths (allow-list). When non-empty, switches to allow-list mode and ignores BlockedFileExtensions.",
                    imp?.AllowedFileExtensions, dImp?.AllowedFileExtensions)
            ]);

        section.IsActive = isActive;
        return section;
    }
}
