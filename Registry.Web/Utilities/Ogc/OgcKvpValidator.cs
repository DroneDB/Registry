using System;
using Microsoft.AspNetCore.Http;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// Validates the common OGC KVP <c>SERVICE</c> and <c>REQUEST</c> parameters per OWS Common 2.0
/// (OGC 06-121r9 §11). Each OGC controller calls these before dispatching so invalid values
/// surface as the OGC-mandated <see cref="OgcException"/> rather than silent capabilities
/// responses or generic 500 errors.
/// </summary>
public static class OgcKvpValidator
{
    /// <summary>
    /// Ensures the <c>SERVICE</c> KVP parameter matches <paramref name="expected"/> (case-insensitive).
    /// Throws <c>MissingParameterValue</c> when absent, <c>InvalidParameterValue</c> when wrong.
    /// </summary>
    public static void ValidateService(IQueryCollection query, string expected)
    {
        var value = OgcRequestParser.Get(query, "SERVICE");
        if (string.IsNullOrWhiteSpace(value))
            throw new OgcException("MissingParameterValue",
                $"Missing required parameter 'service' (expected '{expected}')", 400, "service");
        if (!string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            throw new OgcException("InvalidParameterValue",
                $"Parameter 'service' value '{value}' is not '{expected}'", 400, "service");
    }

    /// <summary>
    /// Ensures the <c>REQUEST</c> KVP parameter is present and is one of the
    /// <paramref name="supported"/> values (case-insensitive). Returns the matched
    /// supported value (canonical casing) so callers can switch on it directly.
    /// </summary>
    public static string ValidateRequest(IQueryCollection query, params string[] supported)
    {
        var value = OgcRequestParser.Get(query, "REQUEST");
        if (string.IsNullOrWhiteSpace(value))
            throw new OgcException("MissingParameterValue",
                "Missing required parameter 'request'", 400, "request");
        foreach (var s in supported)
            if (string.Equals(value, s, StringComparison.OrdinalIgnoreCase))
                return s;
        throw new OgcException("InvalidParameterValue",
            $"Parameter 'request' value '{value}' is not one of [{string.Join(", ", supported)}]",
            400, "request");
    }
}
