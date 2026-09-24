using System.Linq;
using System.Reflection;
using Microsoft.OpenApi;
using Registry.Web.Controllers;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Registry.Web.Filters;

/// <summary>
/// Documents the GET download "path" query parameter as a repeatable string array
/// (<see cref="ObjectsController.Download"/> reads the raw query, not the string binding).
/// </summary>
public class DownloadPathsParameterFilter : IOperationFilter
{
    private static readonly MethodInfo DownloadMethod =
        typeof(ObjectsController).GetMethod(nameof(ObjectsController.Download))!;

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo == DownloadMethod)
            ApplyTo(operation);
    }

    internal static void ApplyTo(OpenApiOperation operation)
    {
        if (operation.Parameters?.FirstOrDefault(p => p.Name == "path") is not OpenApiParameter parameter)
            return;

        parameter.Description =
            "One or more 'path' query parameters: each occurrence is a literal file/folder path " +
            "(names may contain ',' and '&'). A single occurrence may still carry the legacy " +
            "comma-separated list of paths for backward compatibility with already-shared links.";
        parameter.Style = ParameterStyle.Form;
        parameter.Explode = true;
        parameter.Schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Array,
            Items = new OpenApiSchema { Type = JsonSchemaType.String }
        };
    }
}
