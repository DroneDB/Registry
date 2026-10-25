#nullable enable
using System.Linq;
using Registry.Web.Models.Email;

namespace Registry.Web.Services.Email;

/// <summary>What the logs keep of a message's recipients.</summary>
internal static class EmailLog
{
    /// <summary>The distinct recipient domains, e.g. <c>example.com, example.org</c>.</summary>
    public static string Domains(EmailMessage message) =>
        string.Join(", ", message.Recipients()
            .Select(r => r.Address[(r.Address.LastIndexOf('@') + 1)..].Trim().ToLowerInvariant())
            .Distinct());
}
