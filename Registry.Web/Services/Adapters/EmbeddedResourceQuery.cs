using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Embedded resource stream reader for assembly-manifest resources.
/// </summary>
public class EmbeddedResourceQuery
{
    public Stream Read(Assembly assembly, string resource)
    {
        return ReadInternal(assembly, resource);
    }

    private static Stream ReadInternal(Assembly assembly, string resource)
    {
        return assembly.GetManifestResourceStream($"{assembly.GetName().Name}.{resource}");
    }
}