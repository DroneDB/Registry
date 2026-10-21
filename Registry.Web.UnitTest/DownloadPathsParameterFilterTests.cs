using System.Linq;
using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi;
using Moq;
using NUnit.Framework;
using Registry.Web.Controllers;
using Registry.Web.Filters;
using Shouldly;
using Swashbuckle.AspNetCore.Swagger;

namespace Registry.Web.Test;

[TestFixture]
public class DownloadPathsParameterFilterTests
{
    private static OpenApiDocument GenerateDocument()
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.ApplicationName).Returns(typeof(ObjectsController).Assembly.GetName().Name!);
        env.SetupGet(e => e.ContentRootFileProvider).Returns(new NullFileProvider());
        env.SetupGet(e => e.WebRootFileProvider).Returns(new NullFileProvider());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(env.Object);
        services.AddControllers().AddApplicationPart(typeof(ObjectsController).Assembly);
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = "test", Version = "v1" });
            c.OperationFilter<DownloadPathsParameterFilter>();
        });

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
    }

    private static IOpenApiParameter PathParam(OpenApiDocument doc, string route, HttpMethod method) =>
        doc.Paths.Single(p => p.Key.EndsWith(route)).Value.Operations![method].Parameters!
            .Single(p => p.Name == "path");

    [Test]
    public void GeneratedDocument_GetDownload_PathIsExplodedStringArray_OtherDownloadsUntouched()
    {
        var doc = GenerateDocument();

        var getPath = PathParam(doc, "/ds/{dsSlug}/download", HttpMethod.Get);
        getPath.Schema!.Type.ShouldBe(JsonSchemaType.Array);
        getPath.Schema.Items!.Type.ShouldBe(JsonSchemaType.String);
        getPath.Explode.ShouldBeTrue();

        // DownloadExact binds the path from the route: must stay a plain string.
        var exact = PathParam(doc, "/ds/{dsSlug}/download/{path}", HttpMethod.Get);
        exact.Schema!.Type.ShouldNotBe(JsonSchemaType.Array);
    }

    [Test]
    public void ApplyTo_RewritesPathParameter_AsExplodedStringArray()
    {
        // Microsoft.OpenApi v2 does not pre-initialise Parameters: assign the list.
        var operation = new OpenApiOperation
        {
            Parameters =
            [
                new OpenApiParameter
                {
                    Name = "path",
                    In = ParameterLocation.Query,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                }
            ]
        };

        DownloadPathsParameterFilter.ApplyTo(operation);

        var path = operation.Parameters!.Single(p => p.Name == "path");
        path.Schema!.Type.ShouldBe(JsonSchemaType.Array);
        path.Schema.Items!.Type.ShouldBe(JsonSchemaType.String);
        path.Explode.ShouldBeTrue();
        path.Style.ShouldBe(ParameterStyle.Form);
        path.Description.ShouldNotBeNullOrWhiteSpace();
        path.Description.ShouldContain("literal");
    }

    [Test]
    public void ApplyTo_OperationWithoutPathParam_IsANoOp()
    {
        var operation = new OpenApiOperation
        {
            Parameters = [new OpenApiParameter { Name = "inline" }]
        };

        Should.NotThrow(() => DownloadPathsParameterFilter.ApplyTo(operation));

        // A null Parameters list must also be tolerated by the filter.
        Should.NotThrow(() => DownloadPathsParameterFilter.ApplyTo(new OpenApiOperation()));

        operation.Parameters!.Count.ShouldBe(1);
        operation.Parameters[0].Name.ShouldBe("inline");
    }
}
