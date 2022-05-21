using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

public class EmbeddedResourceQuery : IEmbeddedResourceQuery
{
    public Stream Read<T>(string resource)
    {
        var assembly = typeof(T).Assembly;
        return ReadInternal(assembly, resource);
    }

    public Stream Read(Assembly assembly, string resource)
    {
        return ReadInternal(assembly, resource);
    }

    public Stream Read(string assemblyName, string resource)
    {
        var assembly = Assembly.Load(assemblyName);
        return ReadInternal(assembly, resource);
    }

    private static Stream ReadInternal(Assembly assembly, string resource)
    {
        return assembly.GetManifestResourceStream($"{assembly.GetName().Name}.{resource}");
    }

    public string[] GetResourceNames(Assembly assembly)
    {
        var prefix = $"{assembly.GetName().Name}.";
        return assembly.GetManifestResourceNames().Select(item => item[prefix.Length..]).ToArray();
    }
}