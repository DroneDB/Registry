#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;

namespace Registry.Web.Utilities;

/// <summary>
/// Shared retry controls and conservative one-time import of populated legacy caches.
/// Every operation is called while the dataset coordinator lock is held.
/// </summary>
internal static class PendingBuildRetryControlStore
{
    internal const int MaxRetryAttempts = 12;

    internal sealed class Controls
    {
        public int RetryAttempt { get; set; }
        public string? ScheduledRetryJobId { get; set; }
        public string? RetryEpoch { get; set; }
        public bool LegacyImportComplete { get; set; }
    }

    private sealed class LegacyObservation
    {
        public int RetryAttempt { get; set; }
        public string? ScheduledRetryJobId { get; set; }
        public string? RetryEpoch { get; set; }
        public int RetryControlsVersion { get; set; }
    }

    internal static bool HasLegacyControls(byte[]? observation) => ReadLegacy(observation) != null;

    private static Controls? ReadLegacy(byte[]? observation)
    {
        if (observation is not { Length: > 0 }) return null;
        try
        {
            var legacy = JsonSerializer.Deserialize<LegacyObservation>(observation);
            if (legacy == null || legacy.RetryControlsVersion != 0 ||
                (legacy.RetryAttempt <= 0 && string.IsNullOrWhiteSpace(legacy.ScheduledRetryJobId) &&
                 string.IsNullOrWhiteSpace(legacy.RetryEpoch))) return null;
            return new Controls
            {
                RetryAttempt = Math.Clamp(legacy.RetryAttempt, 0, MaxRetryAttempts),
                ScheduledRetryJobId = legacy.ScheduledRetryJobId,
                RetryEpoch = legacy.RetryEpoch
            };
        }
        catch (JsonException) { return null; }
    }

    internal static Controls Load(IStorageConnection connection, string key, byte[]? observation, ILogger logger)
    {
        var json = connection.GetAllEntriesFromHash(key)?.GetValueOrDefault("Controls");
        var controls = new Controls();
        if (json != null)
        {
            try { controls = JsonSerializer.Deserialize<Controls>(json) ?? new Controls(); }
            catch (JsonException ex) { logger.LogWarning(ex, "Malformed shared pending controls; allowing bounded recovery"); }
        }
        controls.RetryAttempt = Math.Clamp(controls.RetryAttempt, 0, MaxRetryAttempts);
        var legacy = controls.LegacyImportComplete ? null : ReadLegacy(observation);
        if (legacy != null)
        {
            // An empty first host must leave import open for another host's
            // populated legacy cache. Never reduce an already shared budget or
            // replace its live coordinator with an older host's observation.
            controls.RetryAttempt = Math.Max(controls.RetryAttempt, legacy.RetryAttempt);
            bool Live(string? id) => !string.IsNullOrWhiteSpace(id) &&
                connection.GetStateData(id)?.Name is "Scheduled" or "Enqueued" or "Processing" or "Awaiting";
            if (string.IsNullOrWhiteSpace(controls.ScheduledRetryJobId) ||
                (!Live(controls.ScheduledRetryJobId) && Live(legacy.ScheduledRetryJobId)))
                controls.ScheduledRetryJobId = legacy.ScheduledRetryJobId;
            controls.RetryEpoch ??= legacy.RetryEpoch;
            controls.LegacyImportComplete = true;
        }
        Save(connection, key, controls);
        return controls;
    }

    internal static void Save(IStorageConnection connection, string key, Controls controls) =>
        connection.SetRangeInHash(key, [new KeyValuePair<string, string>("Controls", JsonSerializer.Serialize(controls))]);
}
