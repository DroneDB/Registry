#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.Email;
using Registry.Web.Services.Email.Templates;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Email;

/// <summary>
/// Renders a template with ASP.NET Core's <see cref="HtmlRenderer"/>: the body component inside
/// <see cref="EmailLayout"/> for the HTML part, the template's text inside <see cref="EmailText"/> for the plain part.
/// </summary>
public sealed class EmailComposer : IEmailComposer
{
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<HubOptions> _hub;
    private readonly IOptions<HttpHostOptions> _http;

    public EmailComposer(IServiceProvider services, ILoggerFactory loggerFactory, IOptions<HubOptions> hub,
        IOptions<HttpHostOptions> http)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <inheritdoc />
    public async Task<EmailMessage> ComposeAsync<TModel>(EmailTemplate<TModel> emailTemplate, TModel model,
        IReadOnlyList<EmailAddress> recipients)
    {
        ArgumentNullException.ThrowIfNull(emailTemplate);
        ArgumentNullException.ThrowIfNull(recipients);

        var branding = EmailBranding.From(_hub.Value, _http.Value);
        var subject = emailTemplate.Subject(model, branding);
        var html = await RenderAsync(emailTemplate.Body, model, branding, subject);
        var text = EmailText.Wrap(emailTemplate.Text(model, branding), branding);

        return new EmailMessage(recipients, subject, text, html);
    }

    private async Task<string> RenderAsync(Type body, object? model, EmailBranding branding, string subject)
    {
        await using var renderer = new HtmlRenderer(_services, _loggerFactory);

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment content = builder =>
            {
                builder.OpenComponent(0, body);
                builder.AddComponentParameter(1, nameof(EmailBody<object>.Model), model);
                builder.AddComponentParameter(2, nameof(EmailBody<object>.Branding), branding);
                builder.CloseComponent();
            };

            var output = await renderer.RenderComponentAsync<EmailLayout>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(EmailLayout.Branding)] = branding,
                    [nameof(EmailLayout.Title)] = subject,
                    [nameof(EmailLayout.ChildContent)] = content
                }));

            return output.ToHtmlString();
        });
    }
}
