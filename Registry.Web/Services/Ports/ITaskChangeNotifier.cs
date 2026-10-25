#nullable enable
using System;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Tells listeners that the task list of a dataset changed: published wherever the task
/// list cache is invalidated (<c>JobIndexWriter</c>), consumed by the task event stream so the Hub
/// refreshes on change instead of polling.
/// </summary>
public interface ITaskChangeNotifier
{
    /// <summary>The task list of the dataset changed.</summary>
    void Publish(string orgSlug, string dsSlug);

    /// <summary>Calls <paramref name="onChange"/> on every change of the dataset's tasks until disposed.</summary>
    IDisposable Subscribe(string orgSlug, string dsSlug, Action onChange);
}
