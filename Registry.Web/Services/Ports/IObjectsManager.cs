using System.IO;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Ports;

/// <summary>
/// The entry operations other features use (shares, CAD coordinate systems, system maintenance): adding a
/// file or a folder, streaming a file to the dataset volume and committing it, deleting an entry. A facade
/// over the objects slice (RQ-11): the object controllers use the focused services in
/// <see cref="Objects"/> (entries, transfers, renders, builds, raster analysis and processing).
/// </summary>
public interface IObjectsManager
{
    /// <summary>Adds the file <paramref name="data"/> at <paramref name="path"/> through the upload pipeline.</summary>
    Task<EntryDto> AddNew(string orgSlug, string dsSlug, string path, byte[] data);

    /// <summary>
    /// Adds the file <paramref name="stream"/> at <paramref name="path"/> through the upload pipeline (validation,
    /// atomic move, index, quarantine on index failure, quota), or creates the folder <paramref name="path"/>
    /// when there is no stream.
    /// </summary>
    Task<EntryDto> AddNew(string orgSlug, string dsSlug, string path, Stream stream = null);

    /// <summary>
    /// Streams a forward-only upload body to a temporary file on the dataset's storage volume,
    /// so it can later be moved (not copied) into place. Used by the streaming upload endpoint.
    /// </summary>
    /// <returns>The temp file, its size and the SHA-256 computed while writing it.</returns>
    Task<StreamedFile> StreamToTempAsync(string orgSlug, string dsSlug, Stream source);

    /// <summary>
    /// Finalizes a streamed upload produced by <see cref="StreamToTempAsync"/>: validates access
    /// and quota, atomically moves the temp file into its final location and indexes it.
    /// </summary>
    /// <returns>The created entry.</returns>
    Task<EntryDto> CommitStreamedAsync(string orgSlug, string dsSlug, string path, StreamedFile file);

    /// <summary>Deletes the entry at <paramref name="path"/> (a folder with everything in it).</summary>
    Task Delete(string orgSlug, string dsSlug, string path);
}
