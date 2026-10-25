#nullable enable
using System;
using Microsoft.AspNetCore.Components;

namespace Registry.Web.Services.Email;

/// <summary>
/// One kind of e-mail: its subject, its plain-text body and its HTML body for a model. The composer puts both
/// bodies inside the shared layout (<see cref="Templates.EmailLayout"/>, <see cref="EmailText"/>), so a template
/// only writes its own content. A new template derives from <see cref="EmailTemplate{TModel, TBody}"/>.
/// </summary>
/// <typeparam name="TModel">The values the e-mail shows.</typeparam>
public abstract class EmailTemplate<TModel>
{
    /// <summary>The subject line.</summary>
    public abstract string Subject(TModel model, EmailBranding branding);

    /// <summary>The plain-text body (the composer adds the signature).</summary>
    public abstract string Text(TModel model, EmailBranding branding);

    /// <summary>The Razor component of the HTML body: an <see cref="EmailBody{TModel}"/>.</summary>
    public abstract Type Body { get; }
}

/// <summary>An <see cref="EmailTemplate{TModel}"/> whose HTML body is the component <typeparamref name="TBody"/>.</summary>
public abstract class EmailTemplate<TModel, TBody> : EmailTemplate<TModel> where TBody : EmailBody<TModel>
{
    /// <inheritdoc />
    public sealed override Type Body => typeof(TBody);
}

/// <summary>
/// The HTML body of a template: a Razor component (<c>@inherits EmailBody&lt;TModel&gt;</c>) that receives the model
/// and the branding. Razor encodes every value it writes, so a model value cannot inject markup. Mail clients drop
/// style sheets: write styles inline.
/// </summary>
public abstract class EmailBody<TModel> : ComponentBase
{
    /// <summary>The values the e-mail shows.</summary>
    [Parameter, EditorRequired]
    public TModel Model { get; set; } = default!;

    /// <summary>The application name and links of this instance.</summary>
    [Parameter, EditorRequired]
    public EmailBranding Branding { get; set; } = default!;
}
