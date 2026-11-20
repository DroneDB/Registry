using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Registry.Web.Test.Support;

/// <summary>Test shorthands over <see cref="HashAlgorithm"/>.</summary>
public static class HashAlgorithmTestExtensions
{
    /// <summary>The hash of the content of the file at <paramref name="inputFile"/>.</summary>
    public static async Task<byte[]> ComputeHashAsync(this HashAlgorithm hashAlgorithm, string inputFile)
    {
        await using var stream = File.OpenRead(inputFile);
        return await hashAlgorithm.ComputeHashAsync(stream);
    }
}
