using System;
using Hangfire;
using Hangfire.Storage;

namespace Registry.Web.Test;

/// <summary>
/// Swaps <see cref="JobStorage.Current"/> for the duration of a test/fixture and restores the
/// previous (null) value on disposal, so that global state set by
/// <c>JobStorage.PerformAddOrUpdate</c> is not leaked into sibling fixtures.
/// </summary>
internal sealed class JobStorageScope : IDisposable
{
    private readonly JobStorage _saved;
    private bool _disposed;

    public JobStorageScope(JobStorage storage)
    {
        // The getter throws when no storage has ever been initialised in this process, so a
        // clean-slate baseline (null) is the legal pre-state.
        try
        {
            _saved = JobStorage.Current;
        }
        catch (InvalidOperationException)
        {
            _saved = null;
        }
        JobStorage.Current = storage;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // None state after: no way to unset the static; every fixture that cares about the
        // value overwrites it with its own TestJobStorage (SetUp/ctor) anyway.
        if (_saved != null)
            JobStorage.Current = _saved;
    }
}
