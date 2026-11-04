using System;
using System.IO;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;

namespace Registry.Web.Services.Push;

/// <summary>The working folder of a push in the dataset's temp folder: the staged files and the metadata dump.</summary>
public static class PushFolders
{
    private const string AddsFolder = "add";
    private const string MetaFile = "meta.json";

    /// <summary>
    /// The folder of the push <paramref name="token"/>. Tokens are the GUIDs Init generates: anything else could point
    /// outside the temp folder.
    /// </summary>
    public static string Of(IDDB ddb, string? token)
    {
        ArgumentNullException.ThrowIfNull(ddb);
        if (!Guid.TryParse(token, out _))
            throw new BadRequestException("Invalid push token");

        return ddb.GetTmpFolder(IDDB.PushFolderPrefix + token);
    }

    /// <summary>The staged files of the push folder <paramref name="pushFolder"/>.</summary>
    public static string Adds(string pushFolder) => Path.Combine(pushFolder, AddsFolder);

    /// <summary>The metadata dump of the push folder <paramref name="pushFolder"/>.</summary>
    public static string Meta(string pushFolder) => Path.Combine(pushFolder, MetaFile);
}
