#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Registry.Ports.Import;

namespace Registry.Web.Services.Import;

/// <summary>
/// Resolves an <see cref="IImportSource"/> by its <see cref="IImportSource.SourceType"/> from the set
/// of registered sources (one per type, SOLID / open-closed).
/// </summary>
public sealed class ImportSourceFactory : IImportSourceFactory
{
    private readonly IReadOnlyDictionary<string, IImportSource> _sources;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportSourceFactory"/> class.
    /// </summary>
    /// <param name="sources">All registered import sources.</param>
    public ImportSourceFactory(IEnumerable<IImportSource> sources)
    {
        _sources = sources.ToDictionary(s => s.SourceType, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> AvailableTypes => _sources.Keys.ToList();

    /// <inheritdoc />
    public IImportSource Resolve(string sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
            throw new ArgumentException("A source type is required.", nameof(sourceType));

        if (_sources.TryGetValue(sourceType, out var source))
            return source;

        throw new ArgumentException($"Unknown import source type '{sourceType}'.");
    }
}
