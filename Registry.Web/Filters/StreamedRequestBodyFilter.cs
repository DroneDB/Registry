using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.OpenApi;
using Registry.Web.Attributes;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Registry.Web.Filters;

/// <summary>
/// The request body of the actions marked with <see cref="StreamedRequestBodyAttribute"/> (the uploads, which
/// read the multipart body themselves): the form fields of the declared type, inline and in order, or a raw
/// binary body (FR-DEV-08).
/// </summary>
public sealed class StreamedRequestBodyFilter : IOperationFilter
{
    private const string MultipartFormData = "multipart/form-data";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.GetCustomAttribute<StreamedRequestBodyAttribute>() is not { } body)
            return;

        var (mediaType, schema) = body.Form is { } form
            ? (MultipartFormData, FormSchema(form, context))
            : (body.MediaType!, new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" });

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = body.Required,
            Content = new Dictionary<string, OpenApiMediaType> { [mediaType] = new() { Schema = schema } }
        };
    }

    private static OpenApiSchema FormSchema(System.Type form, OperationFilterContext context)
    {
        var fields = form.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = fields.ToDictionary(Name,
                p => context.SchemaGenerator.GenerateSchema(p.PropertyType, context.SchemaRepository, memberInfo: p)),
            Required = fields.Where(p => p.GetCustomAttribute<RequiredAttribute>() != null).Select(Name).ToHashSet()
        };
    }

    private static string Name(PropertyInfo field) => JsonNamingPolicy.CamelCase.ConvertName(field.Name);
}
