#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Registry.Ports.Import;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.RemoteImport;

/// <summary>
/// The background job of a remote import: before, the import ran inside the HTTP request. Its return value
/// is the result <c>GET /sys/imports/{jobId}</c> reports; deleting the job cancels the import. Not retried: an import
/// is run again by an administrator (the parallel mode resumes, skipping the files already imported).
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class RemoteImportJob
{
    private readonly RemoteRegistryImporter _importer;
    private readonly IImportCredentialProtector _credentials;

    public RemoteImportJob(RemoteRegistryImporter importer, IImportCredentialProtector credentials)
    {
        _importer = importer;
        _credentials = credentials;
    }

    /// <param name="request">What to import; the password is decrypted here, on the worker.</param>
    /// <param name="cancellationToken">Hangfire's: cancelled when the job is deleted or the server stops.</param>
    public Task<ImportResultDto> RunAsync(RemoteImportRequest request, CancellationToken cancellationToken) =>
        _importer.ImportAsync(request,
            string.IsNullOrEmpty(request.ProtectedPassword) ? null : _credentials.Unprotect(request.ProtectedPassword),
            cancellationToken);
}
