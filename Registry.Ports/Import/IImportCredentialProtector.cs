#nullable enable
namespace Registry.Ports.Import;

/// <summary>
/// Encrypts sensitive task params (passwords, keys) before they are persisted to the JobIndex
/// database and Hangfire storage, and decrypts them on the worker. Backed by ASP.NET Core Data
/// Protection with a SHARED key ring so the processing node can decrypt what the web host
/// encrypted.
/// </summary>
public interface IImportCredentialProtector
{
    /// <summary>Encrypts a plaintext string. Returns a Base64-encoded ciphertext.</summary>
    /// <param name="plaintext">The value to encrypt.</param>
    /// <returns>The protected (encrypted) value.</returns>
    string Protect(string plaintext);

    /// <summary>Decrypts a ciphertext produced by <see cref="Protect"/>. Throws if invalid.</summary>
    /// <param name="ciphertext">The protected value to decrypt.</param>
    /// <returns>The original plaintext.</returns>
    string Unprotect(string ciphertext);
}
