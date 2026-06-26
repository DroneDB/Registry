#nullable enable
using Microsoft.AspNetCore.DataProtection;
using Registry.Ports.Import;

namespace Registry.Web.Services.Import;

/// <summary>
/// <see cref="IImportCredentialProtector"/> backed by ASP.NET Core Data Protection. Uses a
/// dedicated purpose string so import credentials are cryptographically isolated from any other
/// protector. The underlying key ring is shared between the web host and the processing node (see
/// <c>DataProtectionExtensions.AddSharedDataProtection</c>) so the worker can decrypt what the web
/// host encrypted.
/// </summary>
public sealed class ImportCredentialProtector : IImportCredentialProtector
{
    private const string Purpose = "Registry.Import.Credentials.v1";

    private readonly IDataProtector _protector;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportCredentialProtector"/> class.
    /// </summary>
    /// <param name="provider">The shared data protection provider.</param>
    public ImportCredentialProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    /// <inheritdoc />
    public string Protect(string plaintext) => _protector.Protect(plaintext);

    /// <inheritdoc />
    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
