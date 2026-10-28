#nullable enable
using System;
using System.Threading;

namespace Registry.Ports.DroneDB;

/// <summary>
/// What a Gaussian splat gives to other tools: the training data of an ODM project (a COLMAP sparse model
/// with its images and georeference) and the splat as one <c>.spz</c> file. Both run for minutes and read
/// whole files in memory, so a host calls them from a heavy task.
/// </summary>
public interface IDdbSplat
{
    /// <summary>
    /// Writes the training data of the ODM project in <paramref name="folder"/> to <paramref name="outFolder"/>
    /// (<c>DDBExportSplatTrainingData</c>): <c>sparse/0/*.bin</c>, <c>images/</c> and the georeference of the project. Only
    /// reads the dataset; a failed or canceled call may leave a partial <paramref name="outFolder"/>.
    /// </summary>
    /// <param name="folder">The dataset-relative folder of the project ("" for the dataset root).</param>
    /// <param name="outFolder">A folder of the filesystem, outside the project's folder.</param>
    /// <param name="progress">Receives the fraction done and the phase (cameras, points, images, model).</param>
    /// <param name="cancellationToken">Stops the call at its next checkpoint (<see cref="DdbCanceledException"/>).</param>
    /// <exception cref="DdbInvalidArgumentsException">An option is refused or <paramref name="outFolder"/> is the project's.</exception>
    /// <exception cref="DdbNotFoundException">The folder is not in the dataset.</exception>
    /// <exception cref="DdbException">A file of the project is missing or unusable (the message names it).</exception>
    SplatTrainingExport ExportSplatTrainingData(string folder, string outFolder, SplatTrainingOptions? options,
        Action<double, string?>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the <c>.spz</c> of the splat <paramref name="entryPath"/> in the build folder of the entry
    /// (<c>DDBExportSplatSpz</c>), or returns the one that is there and not older than the dataset file.
    /// </summary>
    /// <exception cref="DdbInvalidArgumentsException">The entry is not a splat, or its format is not converted (KSPLAT, SOG, RAD).</exception>
    /// <exception cref="DdbNotFoundException">The entry is not in the dataset or is not built.</exception>
    /// <exception cref="DdbBuildInProgressException">A build or another export of the entry holds its build lock.</exception>
    /// <exception cref="DdbRetryableException">The disk is (nearly) full.</exception>
    SplatSpzExport ExportSplatSpz(string entryPath, CancellationToken cancellationToken);
}
