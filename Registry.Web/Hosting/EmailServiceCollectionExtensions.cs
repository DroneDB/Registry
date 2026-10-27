#nullable enable
using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Registry.Web.HealthChecks;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Email;
using Registry.Web.Services.Email.Templates;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Hosting;

/// <summary>
/// E-mail, for every host that runs jobs or serves requests: a request queues an e-mail and whichever host runs the
/// job sends it, so the web server and the processing nodes register the same services.
/// </summary>
public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// <see cref="IEmailService"/> and its parts. The transport is MailKit when <see cref="SmtpSettings.Enabled"/>,
    /// otherwise <see cref="DisabledEmailSender"/>. The SMTP health check is registered while e-mail is enabled.
    /// </summary>
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.GetRegistryOptions<SmtpSettings>().Enabled)
        {
            services.AddSingleton<IEmailSender, MailKitEmailSender>();
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<SmtpHealthCheck>();
        }
        else
            services.AddSingleton<IEmailSender, DisabledEmailSender>();

        services.AddSingleton<IEmailComposer, EmailComposer>();

        // The address of one user, from the identity database every host that runs jobs shares (no UserManager needed)
        services.AddScoped<IUserRecipients, IdentityUserRecipients>();

        // The e-mails an administrator can edit: their texts live in the storage folder, so every node sends the saved one
        services.AddSingleton<IEditableEmailTemplate>(BuildNotificationEmailTemplate.Instance);
        services.AddSingleton<EditableEmailTemplateStore>();
        services.AddSingleton<EditableEmailTemplates>();
        services.AddSingleton<IBuildNotificationComposer, BuildNotificationComposer>();

        // Scoped: it enqueues through the scoped IBackgroundJobsProcessor
        services.AddScoped<IEmailService, EmailService>();

        // The job class, registered like the others so a missing dependency fails the container validation
        services.AddScoped<SendEmailJob>();

        return services;
    }
}
