#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Registry.Ports.Import;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.Import;

/// <summary>
/// The import sources a server offers (RQ-12, out of the import manager and tool): the registered sources
/// (<see cref="IImportSourceFactory"/>) narrowed by the allow-list of the import settings, with the one wording of
/// why a source type is refused. The web host answers with a 400, the worker with an invalid-operation failure.
/// </summary>
public sealed class ImportSourceCatalog
{
    private readonly IImportSourceFactory _factory;
    private readonly ImportSettings _settings;

    public ImportSourceCatalog(IImportSourceFactory factory, IOptions<ImportSettings> settings)
    {
        _factory = factory;
        _settings = settings.Value;
    }

    /// <summary>The refusal of a source type the allow-list disables.</summary>
    public static string DisabledMessage(string sourceType) =>
        $"The import source type '{sourceType}' is disabled on this server.";

    /// <summary>The registered source types the allow-list lets through.</summary>
    public IReadOnlyList<string> AllowedTypes => [.. _factory.AvailableTypes.Where(IsAllowed)];

    /// <summary>Why <paramref name="sourceType"/> cannot be used (missing, unknown, disabled), or null when it can.</summary>
    public string? Refusal(string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
            return "A source type is required.";

        if (!_factory.AvailableTypes.Contains(sourceType, StringComparer.OrdinalIgnoreCase))
            return $"Unknown import source type '{sourceType}'.";

        return IsAllowed(sourceType) ? null : DisabledMessage(sourceType);
    }

    /// <summary>Whether the allow-list lets <paramref name="sourceType"/> through (registered or not).</summary>
    public bool IsAllowed(string sourceType) => _settings.IsSourceTypeAllowed(sourceType);

    /// <summary>The source of <paramref name="sourceType"/>.</summary>
    public IImportSource Resolve(string sourceType) => _factory.Resolve(sourceType);
}
