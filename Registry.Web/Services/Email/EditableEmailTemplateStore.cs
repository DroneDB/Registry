using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Storage;

namespace Registry.Web.Services.Email;

/// <summary>
/// The texts administrators gave to the editable e-mails, by template name, in the storage folder every node shares
/// (<see cref="MagicStrings.EmailTemplatesFileName"/>): a job on a processing node sends the text the administrator
/// saved on the web host. A template with no entry uses its built-in text.
/// </summary>
public sealed class EditableEmailTemplateStore
{
    private readonly StorageDocument<Dictionary<string, EmailTemplateText>> _file;
    private readonly Lock _gate = new();

    public EditableEmailTemplateStore(IOptions<StorageOptions> storage, IFileSystem fs, ILogger<EditableEmailTemplateStore> logger) =>
        _file = new StorageDocument<Dictionary<string, EmailTemplateText>>(storage, fs, MagicStrings.EmailTemplatesFileName, logger);

    /// <summary>The saved text of <paramref name="name"/>; null when there is none (the built-in text applies).</summary>
    public EmailTemplateText? Get(string name) =>
        _file.Read() is { } templates && templates.TryGetValue(name, out var text) ? text : null;

    /// <summary>Saves <paramref name="text"/> for <paramref name="name"/>.</summary>
    /// <exception cref="StorageDocumentException">The disk refused it.</exception>
    public void Set(string name, EmailTemplateText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Update(templates => templates[name] = text);
    }

    /// <summary>Removes the saved text of <paramref name="name"/>: the built-in text applies again.</summary>
    /// <exception cref="StorageDocumentException">The disk refused it.</exception>
    public void Remove(string name) => Update(templates => templates.Remove(name));

    // Under a lock of this process: two saves of the same node never lose one another's template
    private void Update(Action<Dictionary<string, EmailTemplateText>> change)
    {
        lock (_gate)
        {
            var templates = new Dictionary<string, EmailTemplateText>(_file.Read() ?? [], StringComparer.Ordinal);
            change(templates);
            _file.Write(templates);
        }
    }
}
