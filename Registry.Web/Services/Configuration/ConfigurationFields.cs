#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Configuration;

/// <summary>
/// The field kinds of the admin configuration view (RQ-12, out of <c>ConfigurationDataBuilder</c>): each compares a
/// configured value with its default; a sensitive field only says whether it is set.
/// </summary>
public static class ConfigurationFields
{
    // ---------------------------------------------------------------------------
    // Helper: build a section with IsActive computed from field differences
    // ---------------------------------------------------------------------------

    public static ConfigurationSectionDataDto Section(
        string name, string title, string description,
        List<ConfigurationFieldDataDto> fields)
    {
        var isActive = fields.Any(f =>
        {
            if (f.Sensitive) return f.IsSet;
            return f.CurrentValue != null && f.CurrentValue != f.DefaultValue;
        });
        return new ConfigurationSectionDataDto
        {
            Name = name,
            Title = title,
            Description = description,
            IsActive = isActive,
            Fields = fields
        };
    }

    // ---------------------------------------------------------------------------
    // Helper: build a single field DTO
    // ---------------------------------------------------------------------------

    public static ConfigurationFieldDataDto Field(
        string key, string displayName, string fieldType, string description,
        string? currentValue = null, string? defaultValue = null,
        bool sensitive = false, bool isSet = false,
        string[]? enumOptions = null, int? minValue = null, int? maxValue = null,
        string? unit = null)
    {
        return new ConfigurationFieldDataDto
        {
            Key = key,
            DisplayName = displayName,
            FieldType = fieldType,
            Description = description,
            CurrentValue = sensitive ? null : currentValue,
            DefaultValue = sensitive ? null : defaultValue,
            Sensitive = sensitive,
            IsSet = isSet,
            EnumOptions = enumOptions,
            MinValue = minValue,
            MaxValue = maxValue,
            Unit = unit
        };
    }

    // ---------------------------------------------------------------------------
    // Helper: build a field from a string property comparison
    // ---------------------------------------------------------------------------

    public static ConfigurationFieldDataDto StringField(
        string key, string displayName, string description,
        string currentValue, string defaultValue,
        string fieldType = "text", string? unit = null)
    {
        return Field(key, displayName, fieldType, description,
            currentValue, defaultValue, unit: unit);
    }

    // ---------------------------------------------------------------------------
    // Helper: build a field from an int/long property comparison
    // ---------------------------------------------------------------------------

    public static ConfigurationFieldDataDto NumberField(
        string key, string displayName, string description,
        long currentValue, long defaultValue,
        int? minValue = null, int? maxValue = null, string? unit = null)
    {
        return Field(key, displayName, "number", description,
            currentValue.ToString(CultureInfo.InvariantCulture), defaultValue.ToString(CultureInfo.InvariantCulture),
            minValue: minValue, maxValue: maxValue, unit: unit);
    }

    // ---------------------------------------------------------------------------
    // Helper: build a field from a nullable int/long property comparison
    // ---------------------------------------------------------------------------

    public static ConfigurationFieldDataDto NullableNumberField(
        string key, string displayName, string description,
        long? currentValue, long? defaultValue,
        int? minValue = null, int? maxValue = null, string? unit = null)
    {
        return Field(key, displayName, "number", description,
            currentValue?.ToString(CultureInfo.InvariantCulture), defaultValue?.ToString(CultureInfo.InvariantCulture),
            minValue: minValue, maxValue: maxValue, unit: unit);
    }

    // ---------------------------------------------------------------------------
    // Helper: build a field from a bool property comparison
    // ---------------------------------------------------------------------------

    public static ConfigurationFieldDataDto BoolField(
        string key, string displayName, string description,
        bool currentValue, bool defaultValue)
    {
        return Field(key, displayName, "bool", description,
            currentValue.ToString(), defaultValue.ToString());
    }

    // ---------------------------------------------------------------------------
    // Helper: build a field from a TimeSpan? property comparison
    // ---------------------------------------------------------------------------

    public static ConfigurationFieldDataDto TimeSpanField(
        string key, string displayName, string description,
        TimeSpan? currentValue, TimeSpan? defaultValue)
    {
        return Field(key, displayName, "timespan", description,
            currentValue?.ToString(), defaultValue?.ToString());
    }

    // ---------------------------------------------------------------------------
    // Helper: build a field from a string[] property comparison
    // ---------------------------------------------------------------------------

    public static ConfigurationFieldDataDto ArrayField(
        string key, string displayName, string description,
        string[]? currentValue, string[]? defaultValue)
    {
        var cv = currentValue != null ? string.Join(",", currentValue) : null;
        var dv = defaultValue != null ? string.Join(",", defaultValue) : null;
        return Field(key, displayName, "array", description, cv, dv);
    }

    // ---------------------------------------------------------------------------
    // Helper: build a sensitive field (password, secret, token)
    // ---------------------------------------------------------------------------

    public static ConfigurationFieldDataDto SensitiveField(
        string key, string displayName, string description,
        string currentValue, string defaultValue)
    {
        var isSet = !string.IsNullOrEmpty(currentValue) && currentValue != defaultValue;
        return Field(key, displayName, "password", description,
            sensitive: true, isSet: isSet);
    }

    // ---------------------------------------------------------------------------
    // Helper: build an enum field
    // ---------------------------------------------------------------------------

    public static ConfigurationFieldDataDto EnumField(
        string key, string displayName, string description,
        string currentValue, string defaultValue, string[] enumOptions)
    {
        return Field(key, displayName, "enum", description,
            currentValue, defaultValue, enumOptions: enumOptions);
    }

    // The admin view shows a section as the JSON of appsettings.json: member names as written in the file (no policy),
    // enums by name, indented
    private static readonly JsonSerializerOptions DisplayOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    // ---------------------------------------------------------------------------
    // Helper: build a JSON field (complex object serialized as indented JSON text)
    // ---------------------------------------------------------------------------

    public static ConfigurationFieldDataDto JsonField(
        string key, string displayName, string description,
        object? currentValue, object? defaultValue)
    {
        // Serialize enums by name (e.g. "Disabled") so the generated JSON is human-readable
        // and round-trips cleanly back into appsettings.json.
        var curr = currentValue != null ? JsonSerializer.Serialize(currentValue, DisplayOptions) : null;
        var def = defaultValue != null ? JsonSerializer.Serialize(defaultValue, DisplayOptions) : null;
        return Field(key, displayName, "json", description,
            curr, def, isSet: currentValue != null);
    }
}
