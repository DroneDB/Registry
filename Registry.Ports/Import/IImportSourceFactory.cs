#nullable enable
using System.Collections.Generic;

namespace Registry.Ports.Import;

/// <summary>
/// Resolves an <see cref="IImportSource"/> by its kebab-case <see cref="IImportSource.SourceType"/>.
/// Adding a source is an additive change (one new implementation), never a modification of an
/// existing one.
/// </summary>
public interface IImportSourceFactory
{
    /// <summary>
    /// Resolves the source implementation for the given type.
    /// </summary>
    /// <param name="sourceType">The kebab-case source type identifier.</param>
    /// <returns>The matching <see cref="IImportSource"/>.</returns>
    IImportSource Resolve(string sourceType);

    /// <summary>The set of registered source type identifiers.</summary>
    IReadOnlyList<string> AvailableTypes { get; }
}
