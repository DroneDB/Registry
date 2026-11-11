using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Registry.Web.Utilities;

/// <summary>
/// Binds a <see cref="DatasetPath"/> from the query, the route or the form through
/// <see cref="DatasetPathPolicy.Parse"/>. A missing or blank value binds nothing (as MVC binds an empty string
/// to null): <c>[Required]</c> or the action's default decides. A refused path throws the rule's
/// <see cref="Exceptions.InvalidDatasetPathException"/>, which the API's exception filter answers like the
/// check it replaces (400, <c>noRetry</c>, the rule), before the action and its access check run.
/// </summary>
public sealed class DatasetPathModelBinder : IModelBinder
{
    private readonly DatasetPathPolicy _paths;

    public DatasetPathModelBinder(DatasetPathPolicy paths)
    {
        _paths = paths;
    }

    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var value = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (value == ValueProviderResult.None)
            return Task.CompletedTask;

        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, value);

        if (_paths.Parse(value.FirstValue) is { } path)
            bindingContext.Result = ModelBindingResult.Success(path);
        return Task.CompletedTask;
    }
}

/// <summary>Selects <see cref="DatasetPathModelBinder"/> for <see cref="DatasetPath"/> models.</summary>
public sealed class DatasetPathModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Metadata.ModelType == typeof(DatasetPath)
            ? new BinderTypeModelBinder(typeof(DatasetPathModelBinder))
            : null;
    }
}

/// <summary>
/// Reads a <see cref="DatasetPath"/> from a JSON request body through <see cref="DatasetPathPolicy.Parse"/> (null,
/// blank: no path; a number: its text). A refused path throws the rule's
/// <see cref="Exceptions.InvalidDatasetPathException"/>, which the input formatter lets through (it records only
/// JSON errors as model errors) to the API's exception filter; an object or an array is a JSON error.
/// </summary>
public sealed class DatasetPathJsonConverter : JsonConverter<DatasetPath>
{
    private readonly DatasetPathPolicy _paths;

    public DatasetPathJsonConverter(DatasetPathPolicy paths)
    {
        _paths = paths;
    }

    public override DatasetPath? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => _paths.Parse(reader.GetString()),
            JsonTokenType.Number => _paths.Parse(Encoding.UTF8.GetString(reader.ValueSpan)),
            JsonTokenType.True => _paths.Parse("True"),
            JsonTokenType.False => _paths.Parse("False"),
            _ => throw new JsonException($"A dataset path is a string, not {reader.TokenType}.")
        };

    public override void Write(Utf8JsonWriter writer, DatasetPath? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.ToString());
    }
}
