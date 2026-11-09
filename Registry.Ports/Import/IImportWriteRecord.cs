using System.Threading;
using System.Threading.Tasks;

namespace Registry.Ports.Import;

/// <summary>
/// The write record of the task an import runs in (FR-XC-27, D-045). A source writes into the dataset folder only
/// through it: it asks <see cref="MayWrite"/>, records a file (<see cref="Record"/>) or a folder
/// (<see cref="RecordFolder"/>) BEFORE it creates it, creates it without replacing anything that appeared meanwhile
/// (<c>FileMode.CreateNew</c>, a move that does not overwrite) and, when that finds a file, drops the path again
/// (<see cref="ForgetAsync"/>) and skips it. A cancelled or failed import then deletes only what the record holds,
/// never a file of somebody else. Paths are dataset-relative, '/'-separated.
/// </summary>
public interface IImportWriteRecord
{
    /// <summary>
    /// Whether the file at <paramref name="path"/> may be written: nothing is there, or the task itself wrote it (an
    /// earlier attempt). Anything else is kept, and the source skips the file.
    /// </summary>
    bool MayWrite(string path);

    /// <summary>Records the file <paramref name="path"/>, about to be created.</summary>
    void Record(string path);

    /// <summary>Records the folder <paramref name="path"/>, about to be created (a rollback removes it when empty).</summary>
    void RecordFolder(string path);

    /// <summary>
    /// The file <paramref name="path"/> is complete: its size and time are recorded, so that a rollback never deletes
    /// it once somebody else replaced it.
    /// </summary>
    void Written(string path);

    /// <summary>The file <paramref name="path"/> could not be created after all (it appeared meanwhile): it is not the task's.</summary>
    Task ForgetAsync(string path, CancellationToken ct);

    /// <summary>Writes the buffered records when a batch is due; called between files.</summary>
    ValueTask FlushIfDueAsync(CancellationToken ct);
}
