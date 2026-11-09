using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;
using Registry.Web.Utilities.Ogc;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Registry.Web.Filters;

/// <summary>
/// The credentials and the refusals of each operation in the API reference, read from the authorization
/// metadata of its action (FR-DEV-08), so the reference can sign requests in and lists the denials the
/// server answers:
/// <list type="bullet">
/// <item><c>[AllowAnonymous]</c>: no security requirement.</item>
/// <item><c>[Authorize]</c>: a bearer token, and a 401; the <see cref="Policies.Admin"/> policy also a 403.</item>
/// <item>No gate: a bearer token or nothing (public datasets are read anonymously), also HTTP Basic where
/// <see cref="BasicAuthenticationAttribute"/> authenticates; an operation on an organization or a dataset
/// (an <c>orgSlug</c> route value) answers 401 and 403 from its access check.</item>
/// </list>
/// The 401 and 403 are ProblemDetails, except where <see cref="OgcAuthorizationFilter"/> answers in the OGC
/// envelope (its actions declare their own). Every ProblemDetails response is described as
/// <c>application/problem+json</c>, the media type of every error body. Status codes that only some actions
/// return (413, 429, 507) stay <c>[ProducesResponseType]</c> attributes on those actions.
/// </summary>
public sealed class ApiSecurityOperationFilter : IOperationFilter
{
    /// <summary>Name of the JWT bearer security scheme.</summary>
    public const string BearerScheme = "bearer";

    /// <summary>Name of the HTTP Basic security scheme (OGC services, MVT and build files).</summary>
    public const string BasicScheme = "basic";

    private const string OrganizationRouteValue = "orgSlug";

    private static readonly string[] ProblemSchemas = [nameof(ProblemDetails), nameof(ValidationProblemDetails)];

    /// <summary>The two schemes of the document: a JWT from <c>POST /users/authenticate</c>, and HTTP Basic.</summary>
    public static void AddSecuritySchemes(SwaggerGenOptions options)
    {
        options.AddSecurityDefinition(BearerScheme, new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "The token of POST /users/authenticate, sent as 'Authorization: Bearer <token>'."
        });
        options.AddSecurityDefinition(BasicScheme, new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "basic",
            Description = "User name and password; accepted by the OGC services, the vector tiles and the build files."
        });
    }

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        var authorize = metadata.OfType<IAuthorizeData>().ToList();

        if (!metadata.OfType<IAllowAnonymous>().Any())
        {
            if (authorize.Count > 0)
            {
                operation.Security = [Requirement(context, BearerScheme)];
                AddDenial(operation, context, StatusCodes.Status401Unauthorized);
                if (authorize.Any(a => a.Policy == Policies.Admin))
                    AddDenial(operation, context, StatusCodes.Status403Forbidden);
            }
            else
            {
                operation.Security = [new OpenApiSecurityRequirement(), Requirement(context, BearerScheme)];
                if (metadata.OfType<BasicAuthenticationAttribute>().Any())
                    operation.Security.Add(Requirement(context, BasicScheme));

                var ogcEnvelope = metadata.OfType<ServiceFilterAttribute>()
                    .Any(f => f.ServiceType == typeof(OgcAuthorizationFilter));
                if (!ogcEnvelope && context.ApiDescription.ParameterDescriptions.Any(p =>
                        p.Source == BindingSource.Path && p.Name == OrganizationRouteValue))
                {
                    AddDenial(operation, context, StatusCodes.Status401Unauthorized);
                    AddDenial(operation, context, StatusCodes.Status403Forbidden);
                }
            }
        }

        DescribeProblemsAsProblemJson(operation);
    }

    private static OpenApiSecurityRequirement Requirement(OperationFilterContext context, string scheme) =>
        new() { [new OpenApiSecuritySchemeReference(scheme, context.Document)] = [] };

    private static void AddDenial(OpenApiOperation operation, OperationFilterContext context, int status)
    {
        operation.Responses ??= new OpenApiResponses();
        var key = status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // A declared denial keeps its body; one declared without a body ([ProducesResponseType(401)]) gets ours
        if (operation.Responses.TryGetValue(key, out var declared) && declared.Content is { Count: > 0 })
            return;

        operation.Responses[key] = new OpenApiResponse
        {
            Description = declared?.Description ?? ReasonPhrases.GetReasonPhrase(status),
            Content = new Dictionary<string, OpenApiMediaType>
            {
                [ApiErrorResponder.ProblemJsonContentType] = new()
                {
                    Schema = context.SchemaGenerator.GenerateSchema(typeof(ProblemDetails), context.SchemaRepository)
                }
            }
        };
    }

    private static void DescribeProblemsAsProblemJson(OpenApiOperation operation)
    {
        if (operation.Responses == null)
            return;

        foreach (var (status, response) in operation.Responses)
        {
            if (!int.TryParse(status, out var code) || code < StatusCodes.Status400BadRequest ||
                response is not OpenApiResponse { Content: { } content } ||
                !content.TryGetValue("application/json", out var media) ||
                media.Schema is not OpenApiSchemaReference { Reference.Id: { } id } ||
                !ProblemSchemas.Contains(id, StringComparer.Ordinal))
                continue;

            content.Remove("application/json");
            content[ApiErrorResponder.ProblemJsonContentType] = media;
        }
    }
}
