using System.Collections.Generic;
using Registry.Web.Utilities;

namespace Registry.Web.Exceptions;

/// <summary>
/// A request parameter that is refused (HTTP 400): a <see cref="RegistryException.FieldErrors"/> refusal of that one
/// field, so <see cref="ApiErrorResponder"/> writes the message as the <c>detail</c> and under <c>errors</c> with the name
/// of the parameter (<c>errors.x</c>), and a client can show it at the field.
/// </summary>
public sealed class InvalidFieldException : BadRequestException
{
    /// <summary>Creates the refusal of the parameter <paramref name="field"/>.</summary>
    /// <param name="field">The name of the parameter, as the request sends it (<c>x</c>).</param>
    /// <param name="message">Why it is refused.</param>
    public InvalidFieldException(string field, string message) : base(message, noRetry: true)
    {
        Field = field;
        FieldErrors = new Dictionary<string, string[]> { [field] = [message] };
    }

    /// <summary>The name of the refused parameter.</summary>
    public string Field { get; }
}
