using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.Email;
using Registry.Web.Services.Email;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Sends e-mail: the one interface a feature depends on. Queue by default. The queued job retries while the
/// server is unreachable, and the request does not wait for it. Send now only when the caller must know the
/// result, as the admin test e-mail does.
/// While e-mail is disabled (<c>AppSettings:Smtp:Enabled</c>), every call logs the e-mail and drops it, so a
/// feature never has to check first; <see cref="IsEnabled"/> is for the UI that should not offer it.
/// </summary>
public interface IEmailService
{
    /// <summary>Whether e-mail is configured and on.</summary>
    bool IsEnabled { get; }

    /// <summary>Renders <paramref name="emailTemplate"/> for <paramref name="model"/> and queues it to <paramref name="recipients"/>.</summary>
    /// <returns>The id of the job that sends it, or null when e-mail is disabled.</returns>
    Task<string?> QueueAsync<TModel>(EmailTemplate<TModel> emailTemplate, TModel model, IReadOnlyList<EmailAddress> recipients,
        CancellationToken ct = default);

    /// <summary>Queues <paramref name="message"/>.</summary>
    /// <returns>The id of the job that sends it, or null when e-mail is disabled.</returns>
    Task<string?> QueueAsync(EmailMessage message, CancellationToken ct = default);

    /// <summary>Renders <paramref name="emailTemplate"/> for <paramref name="model"/> and sends it to <paramref name="recipients"/> now.</summary>
    /// <returns>False when e-mail is disabled (nothing was sent).</returns>
    /// <exception cref="Exceptions.EmailDeliveryException">The server refused the e-mail or could not be reached.</exception>
    Task<bool> SendNowAsync<TModel>(EmailTemplate<TModel> emailTemplate, TModel model, IReadOnlyList<EmailAddress> recipients,
        CancellationToken ct = default);

    /// <summary>Sends <paramref name="message"/> now.</summary>
    /// <returns>False when e-mail is disabled (nothing was sent).</returns>
    /// <exception cref="Exceptions.EmailDeliveryException">The server refused the e-mail or could not be reached.</exception>
    Task<bool> SendNowAsync(EmailMessage message, CancellationToken ct = default);
}
