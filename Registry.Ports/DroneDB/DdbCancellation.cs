using System;
using System.Threading;

namespace Registry.Ports.DroneDB;

/// <summary>
/// The cancellation the current native work runs for (RQ-13, XI-07), as a token that flows with the async
/// context, like <see cref="DdbOperation"/>: the libddb adapter hands it to libddb (a cancel token set on the
/// calling thread right before the call) for each call of an export that honours cancel tokens, so a canceled
/// request or job stops the native work at its next checkpoint. Entered where request or job work becomes
/// native work: the native work gate, the job filter, the thumbnail host.
/// </summary>
public static class DdbCancellation
{
    private static readonly AsyncLocal<CancellationToken> CurrentToken = new();

    /// <summary>The token of the current native work; <see cref="CancellationToken.None"/> for none.</summary>
    public static CancellationToken Current => CurrentToken.Value;

    /// <summary>
    /// Makes <paramref name="token"/> the current cancellation until the returned scope is disposed, which
    /// restores the previous one. When a token is current already (a gated render inside a job), the work is
    /// canceled by either: the two are linked for the scope. A token that cannot be canceled changes nothing.
    /// Enter and dispose in the same flow (both synchronous, or both in one async method).
    /// </summary>
    public static Scope Enter(CancellationToken token)
    {
        var previous = CurrentToken.Value;
        if (!token.CanBeCanceled || token == previous)
            return default;

        if (!previous.CanBeCanceled)
        {
            CurrentToken.Value = token;
            return new Scope(null, previous);
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(previous, token);
        CurrentToken.Value = linked.Token;
        return new Scope(linked, previous);
    }

    /// <summary>Restores the previous cancellation when disposed.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly CancellationToken _previous;
        private readonly CancellationTokenSource? _linked;
        private readonly bool _entered;

        internal Scope(CancellationTokenSource? linked, CancellationToken previous)
        {
            _previous = previous;
            _linked = linked;
            _entered = true;
        }

        public void Dispose()
        {
            if (!_entered)
                return;

            CurrentToken.Value = _previous;
            _linked?.Dispose();
        }
    }
}
