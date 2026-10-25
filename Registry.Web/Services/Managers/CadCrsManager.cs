#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers;

/// <inheritdoc />
public sealed class CadCrsManager(
    IDatasetAccessResolver access,
    IDdbWrapper ddbWrapper,
    IObjectsManager objectsManager,
    ILogger<CadCrsManager> logger)
{
    /// <summary>
    /// Gives the drawing at <paramref name="path"/> the coordinate system <paramref name="crs"/>: an
    /// authority code ("EPSG:32632"), WKT or PROJ string, or "local" to keep it in drawing
    /// coordinates. Returns the .prj entry.
    /// </summary>
    public async Task<EntryDto> SetAsync(string orgSlug, string dsSlug, string path, string crs,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new BadRequestException("Missing path");
        if (string.IsNullOrWhiteSpace(crs))
            throw new BadRequestException("Missing coordinate system");
        if (Path.GetExtension(path).ToLowerInvariant() is not (".dxf" or ".dwg"))
            throw new BadRequestException("Only CAD drawings (.dxf, .dwg) take a coordinate system");

        // The token is honoured until the change starts: the access check
        var ddb = (await access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct)).Ddb;

        var entry = ddb.GetEntry(path)
                    ?? throw new NotFoundException($"Cannot find '{path}'");

        string prj;
        try
        {
            prj = ddbWrapper.MakeCadPrj(crs);
        }
        catch (ArgumentException ex)
        {
            throw new BadRequestException(ex.Message);
        }

        var prjPath = Path.ChangeExtension(entry.Path, ".prj");
        logger.LogInformation("Setting the coordinate system of {Org}/{Ds}/{Path} to {Crs}", orgSlug, dsSlug,
            entry.Path, crs);

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(prj));
        return await objectsManager.AddNew(orgSlug, dsSlug, prjPath, stream);
    }
}
