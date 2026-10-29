using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Registry.Web.Test.Support;

/// <summary>
/// The host's own JSON output and input formatters, driven without a request: what MVC writes for an
/// action result, and how MVC reads a JSON request body into a parameter. The wire tests use it so the stack
/// under test is whichever one the host registers, never a copy of its settings.
/// </summary>
public static class MvcJson
{
    private const string ContentType = "application/json";

    /// <summary>The body MVC writes for <paramref name="value"/> (the runtime type, as an action result does).</summary>
    public static async Task<string> WriteAsync(IServiceProvider services, object? value)
    {
        var mvc = services.GetRequiredService<IOptions<MvcOptions>>().Value;
        var http = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        http.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));

        var context = new OutputFormatterWriteContext(http, (stream, encoding) => new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true),
            value?.GetType() ?? typeof(object), value) { ContentType = new Microsoft.Extensions.Primitives.StringSegment(ContentType) };
        var formatter = mvc.OutputFormatters.OfType<TextOutputFormatter>().First(f => f.CanWriteResult(context));
        await formatter.WriteAsync(context);

        return Encoding.UTF8.GetString(body.ToArray());
    }

    /// <summary>The result of binding <paramref name="json"/> to a <paramref name="type"/> parameter from the body.</summary>
    public static async Task<BodyBinding> ReadAsync(IServiceProvider services, Type type, string json)
    {
        var mvc = services.GetRequiredService<IOptions<MvcOptions>>().Value;
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(type);
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.ContentType = ContentType;
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var state = new ModelStateDictionary();
        var context = new InputFormatterContext(http, "body", state, metadata,
            (stream, encoding) => new StreamReader(stream, encoding));
        var formatter = mvc.InputFormatters.OfType<TextInputFormatter>().First(f => f.CanRead(context));
        var result = await formatter.ReadAsync(context);

        return new BodyBinding(result.HasError, result.IsModelSet ? result.Model : null,
            string.Join("; ", state.Where(e => e.Value!.Errors.Count > 0)
                .Select(e => $"{e.Key}: {string.Join(", ", e.Value!.Errors.Select(x => x.ErrorMessage))}")));
    }

    public static async Task<T?> ReadAsync<T>(IServiceProvider services, string json)
    {
        var binding = await ReadAsync(services, typeof(T), json);
        binding.HasError.ShouldBeFalse(binding.Errors);
        return (T?)binding.Model;
    }

    public sealed record BodyBinding(bool HasError, object? Model, string Errors);
}
