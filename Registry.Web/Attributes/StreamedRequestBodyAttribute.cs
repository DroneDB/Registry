using System;

namespace Registry.Web.Attributes;

/// <summary>
/// Describes in the API reference the request body of an action that reads it itself (streamed uploads,
/// <see cref="DisableFormValueModelBindingAttribute"/>), so it has no parameter to describe it:
/// <c>multipart/form-data</c> with the fields of <see cref="Form"/> (its public properties, in their
/// order, with their XML documentation), or a raw body of <see cref="MediaType"/>.
/// Read by <c>StreamedRequestBodyFilter</c>; it changes nothing at run time.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class StreamedRequestBodyAttribute : Attribute
{
    /// <summary>A <c>multipart/form-data</c> body with the fields of <paramref name="form"/>.</summary>
    public StreamedRequestBodyAttribute(Type form)
    {
        Form = form;
    }

    /// <summary>A raw body of <paramref name="mediaType"/>.</summary>
    public StreamedRequestBodyAttribute(string mediaType)
    {
        MediaType = mediaType;
    }

    /// <summary>The type whose properties are the form fields, or null for a raw body.</summary>
    public Type? Form { get; }

    /// <summary>The media type of a raw body, or null for a form.</summary>
    public string? MediaType { get; }

    /// <summary>Whether the request must have a body (default true).</summary>
    public bool Required { get; init; } = true;
}
