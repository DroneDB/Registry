using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.Email;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Email;

/// <summary>
/// <see cref="IEmailService"/>: composes, checks the message, then queues it (<see cref="SendEmailJob"/>) or
/// sends it through <see cref="IEmailSender"/>. While e-mail is disabled it logs the e-mail and drops it before
/// anything is composed.
/// </summary>
public sealed class EmailService : IEmailService
{
    private readonly IOptions<SmtpSettings> _smtp;
    private readonly IEmailComposer _composer;
    private readonly IEmailSender _sender;
    private readonly IBackgroundJobsProcessor _jobs;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IOptions<SmtpSettings> smtp, IEmailComposer composer, IEmailSender sender,
        IBackgroundJobsProcessor jobs, ILogger<EmailService> logger)
    {
        _smtp = smtp ?? throw new ArgumentNullException(nameof(smtp));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsEnabled => _smtp.Value.Enabled;

    /// <inheritdoc />
    public async Task<string?> QueueAsync<TModel>(EmailTemplate<TModel> emailTemplate, TModel model,
        IReadOnlyList<EmailAddress> recipients, CancellationToken ct = default)
    {
        var message = await ComposeIfEnabledAsync(emailTemplate, model, recipients);
        return message == null ? null : Queue(message);
    }

    /// <inheritdoc />
    public Task<string?> QueueAsync(EmailMessage message, CancellationToken ct = default) =>
        Task.FromResult(Accepts(message) ? Queue(message) : null);

    /// <inheritdoc />
    public async Task<bool> SendNowAsync<TModel>(EmailTemplate<TModel> emailTemplate, TModel model,
        IReadOnlyList<EmailAddress> recipients, CancellationToken ct = default)
    {
        var message = await ComposeIfEnabledAsync(emailTemplate, model, recipients);
        return message != null && await SendAsync(message, ct);
    }

    /// <inheritdoc />
    public async Task<bool> SendNowAsync(EmailMessage message, CancellationToken ct = default) =>
        Accepts(message) && await SendAsync(message, ct);

    private async Task<EmailMessage?> ComposeIfEnabledAsync<TModel>(EmailTemplate<TModel> emailTemplate, TModel model,
        IReadOnlyList<EmailAddress> recipients)
    {
        ArgumentNullException.ThrowIfNull(emailTemplate);
        ArgumentNullException.ThrowIfNull(recipients);

        if (IsEnabled)
            return await _composer.ComposeAsync(emailTemplate, model, recipients);

        LogDropped(emailTemplate.GetType().Name, recipients.Count);
        return null;
    }

    private bool Accepts(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (IsEnabled)
            return true;

        LogDropped(message.Subject, message.Recipients().Count());
        return false;
    }

    private string Queue(EmailMessage message)
    {
        Check(message);

        var jobId = _jobs.Enqueue<SendEmailJob>(job => job.SendAsync(message, CancellationToken.None));
        _logger.LogInformation("E-mail \"{Subject}\" to {RecipientCount} recipient(s) queued as job {JobId}",
            message.Subject, message.Recipients().Count(), jobId);
        return jobId;
    }

    private async Task<bool> SendAsync(EmailMessage message, CancellationToken ct)
    {
        Check(message);

        await _sender.SendAsync(message, ct);
        return true;
    }

    private void LogDropped(string what, int recipients) =>
        _logger.LogInformation("E-mail is disabled (AppSettings:Smtp:Enabled): \"{Email}\" to {RecipientCount} recipient(s) not sent",
            what, recipients);

    /// <summary>A message the server can take: at least one recipient, every address one mailbox, a subject.</summary>
    private static void Check(EmailMessage message)
    {
        if (!message.Recipients().Any())
            throw new BadRequestException("The e-mail has no recipient", noRetry: true);

        var addresses = message.ReplyTo == null ? message.Recipients() : message.Recipients().Append(message.ReplyTo);
        var invalid = addresses.Select(a => a.Address).Where(address => !EmailAddress.IsValid(address)).ToList();
        if (invalid.Count > 0)
            throw new BadRequestException($"Not an e-mail address: {string.Join(", ", invalid)}", noRetry: true);

        if (string.IsNullOrWhiteSpace(message.Subject))
            throw new BadRequestException("The e-mail has no subject", noRetry: true);
    }
}
