#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Registry.Ports.DroneDB;

namespace Registry.Web.Models;

/// <summary>
/// State of a push between Init and Commit, kept in the push folder.
/// </summary>
public class PushSession
{
    /// <summary>
    /// Checksum of the dataset stamp when the push started: Commit applies <see cref="Delta"/>
    /// only if the dataset has not changed since.
    /// </summary>
    [JsonProperty("ourChecksum")]
    public required string OurChecksum { get; init; }

    /// <summary>Delta from the dataset to the client's stamp, computed once by Init.</summary>
    [JsonProperty("delta")]
    public required Delta Delta { get; init; }

    private IReadOnlyDictionary<string, string>? _addedFiles;

    /// <summary>
    /// The SHA-256 the delta gives the file it adds at <paramref name="path"/> (the delta's exact
    /// spelling), or null when the delta adds no file there (a folder has no hash).
    /// </summary>
    public string? AddedFileHash(string path)
    {
        _addedFiles ??= Delta.Adds.Where(a => a.Hash.Length > 0)
            .GroupBy(a => a.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Hash, StringComparer.Ordinal);
        return _addedFiles.GetValueOrDefault(path);
    }
}
