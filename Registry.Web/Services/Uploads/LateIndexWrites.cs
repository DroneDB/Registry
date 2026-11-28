using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Uploads;

/// <summary>
/// The files of one upload request whose index write was still running when the request stopped waiting for it
/// (<see cref="IndexWritePendingException"/>). They stay in the dataset until the index queue tells their outcome
/// (<see cref="Tell"/>, the <see cref="LateIndexOutcome"/> of the request): a file that was indexed keeps its size in
/// the quota; one that was not is taken out as the request would have taken it out at once
/// (<see cref="UploadFiles.DropUnindexedLate"/>), so it does not stay as an unindexed file that refuses the retry, and
/// its size is given back. The queue may tell an outcome before the request has finished with its own ones:
/// <see cref="Await"/> and <see cref="Tell"/> meet in either order.
/// </summary>
internal sealed class LateIndexWrites
{
    private readonly UploadRequestContext _ctx;
    private readonly IReadOnlyList<MovedUploadPart> _parts;
    private readonly UploadFiles _files;
    private readonly ILogger _logger;

    private readonly Lock _gate = new();
    private readonly Dictionary<int, bool> _stays = [];
    private IReadOnlyDictionary<int, long>? _awaited;
    private IQuotaReservation? _reservation;
    private bool _settled;

    /// <param name="ctx">The request.</param>
    /// <param name="parts">The files the request indexes, in the order they are enqueued (an item of the queue is a
    /// position in it).</param>
    /// <param name="files">What is done with the files on disk.</param>
    /// <param name="logger">Logger.</param>
    public LateIndexWrites(UploadRequestContext ctx, IReadOnlyList<MovedUploadPart> parts, UploadFiles files,
        ILogger logger)
    {
        _ctx = ctx;
        _parts = parts;
        _files = files;
        _logger = logger;
    }

    /// <summary>Whether <paramref name="part"/> waits for the outcome the queue tells later.</summary>
    public static bool Awaits(MovedUploadPart part) => part.Error is IndexWritePendingException;

    /// <summary>
    /// The outcome of the index write of file <paramref name="item"/>, told by the queue once the batch that held it
    /// ended: the file is left as an entry, or taken out; the commit is then done with its temp file.
    /// </summary>
    public void Tell(int item, IndexOutcome outcome)
    {
        var moved = _parts[item];
        bool stays;
        if (outcome.Entry != null)
        {
            _logger.LogInformation("'{Path}' in dataset '{OrgSlug}/{DsSlug}' was indexed after its upload stopped " +
                                   "waiting", moved.Part.Path, _ctx.OrgSlug, _ctx.DsSlug);
            stays = true;
        }
        else
            stays = _files.DropUnindexedLate(_ctx, moved,
                outcome.Error ?? new InvalidOperationException($"'{moved.Part.Path}' has no index outcome"));

        _files.DeleteLeftover(_ctx, moved.Part.File);

        lock (_gate)
        {
            _stays[item] = stays;
            Settle();
        }
    }

    /// <summary>
    /// The request is done with its own outcomes: <paramref name="awaited"/> are the files (by item, with their sizes)
    /// whose outcome the queue tells later, <paramref name="reservation"/> holds their sizes in the quota (null: no
    /// quota is held). Once every one is told, the reservation keeps the size of those that stay.
    /// </summary>
    public void Await(IReadOnlyDictionary<int, long> awaited, IQuotaReservation? reservation)
    {
        lock (_gate)
        {
            _awaited = awaited;
            _reservation = reservation;
            Settle();
        }
    }

    // Under the gate
    private void Settle()
    {
        if (_settled || _awaited is not { } awaited || awaited.Keys.Any(item => !_stays.ContainsKey(item)))
            return;

        _settled = true;
        if (_reservation is not { } reservation)
            return;

        var staying = awaited.Where(a => _stays[a.Key]).Sum(a => a.Value);
        if (staying == 0)
        {
            reservation.Dispose();
            return;
        }

        reservation.Resize(staying);
        reservation.Commit();
    }
}
