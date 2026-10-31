using System.IO;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test.Support;

/// <summary>Test shorthands over <see cref="IObjectsManager"/>.</summary>
public static class ObjectsManagerTestExtensions
{
    /// <summary>Adds a file with the given content, the way an upload of it ends.</summary>
    public static async Task<EntryDto> AddNew(this IObjectsManager objects, string orgSlug, string dsSlug, string path,
        byte[] data)
    {
        await using var stream = new MemoryStream(data);
        return await objects.AddNew(orgSlug, dsSlug, path, stream);
    }
}
