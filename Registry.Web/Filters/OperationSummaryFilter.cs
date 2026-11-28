using System.Text.RegularExpressions;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Registry.Web.Filters;

/// <summary>
/// The title of each operation in the API reference is one short line (FR-DEV-12): the first sentence of the
/// action's XML <c>summary</c>, on one line and without Markdown code marks (a title shows them raw). The
/// rest of the summary opens the description, before the <c>remarks</c>.
/// </summary>
public sealed partial class OperationSummaryFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (string.IsNullOrWhiteSpace(operation.Summary))
            return;

        var (title, rest) = Split(operation.Summary);
        operation.Summary = title;
        if (rest.Length > 0)
            operation.Description = string.IsNullOrWhiteSpace(operation.Description)
                ? rest
                : rest + "\n\n" + operation.Description;
    }

    /// <summary>The first sentence of <paramref name="summary"/> as a title, and the rest.</summary>
    internal static (string Title, string Remainder) Split(string summary)
    {
        var text = summary.Trim();
        // A sentence ends at '.', '!' or '?', then whitespace, then a capital letter: "i.e. builds", "e.g. To",
        // "etc. The", "vs. JSON" go on, and so does a dot inside a token ("v2.0 API", "1.5 MB", a URL)
        var end = SentenceEnd().Match(text);
        var first = end.Success ? text[..end.Index] : text;
        var rest = end.Success ? text[(end.Index + end.Length)..].Trim() : string.Empty;
        return (Whitespace().Replace(first, " ").Replace("`", string.Empty), rest);
    }

    [GeneratedRegex(@"(?<=[.!?])(?<!\b(?i:e\.g|i\.e|etc|vs)\.)\s+(?=[A-Z])")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
