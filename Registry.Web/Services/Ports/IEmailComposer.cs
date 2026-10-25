#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Registry.Web.Models.Email;
using Registry.Web.Services.Email;

namespace Registry.Web.Services.Ports;

/// <summary>Turns a template and its model into a message, inside the shared layout.</summary>
public interface IEmailComposer
{
    /// <summary>The message of <paramref name="emailTemplate"/> for <paramref name="model"/>, to <paramref name="recipients"/>.</summary>
    Task<EmailMessage> ComposeAsync<TModel>(EmailTemplate<TModel> emailTemplate, TModel model, IReadOnlyList<EmailAddress> recipients);
}
