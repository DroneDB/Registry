#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;

namespace Registry.Web.Utilities;

/// <summary>
/// The one writer of Registry error responses (RQ-05, plan decision D1: RFC 9457 ProblemDetails). For an
/// exception it classifies once (<see cref="ApiExceptionClassifier"/>), logs once at the classifier's
/// level, sets <c>Retry-After</c> and builds the body; its callers only deliver it:
/// <see cref="ApiExceptionFilter"/> (MVC actions), <see cref="ApiExceptionHandler"/> (middleware, minimal
/// endpoints, resource filters, the SPA fallback) and the model-state factory in <c>Startup</c>.
/// <para>Body: <c>type</c>, <c>title</c>, <c>status</c>, <c>detail</c> (the client-facing message) and
/// <c>traceId</c> from MVC's <see cref="ProblemDetailsFactory"/>, plus the <see cref="NoRetryMember"/>
/// extension member.</para>
/// </summary>
public sealed class ApiErrorResponder
{
    /// <summary>Extension member: true when the same request can never succeed.</summary>
    public const string NoRetryMember = "noRetry";

    /// <summary>RFC 9457 media type of every error body.</summary>
    public const string ProblemJsonContentType = "application/problem+json";

    /// <summary>Detail of a model-state failure that carries no message.</summary>
    public const string InvalidRequestMessage = "Invalid";

    private readonly ProblemDetailsFactory _factory;
    private readonly ILogger<ApiErrorResponder> _logger;

    public ApiErrorResponder(ProblemDetailsFactory factory, ILogger<ApiErrorResponder> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>
    /// Classifies and logs <paramref name="exception"/>, sets <c>Retry-After</c>, and returns the outcome:
    /// the descriptor and the body (null for a 408: the client went away, nobody reads it).
    /// </summary>
    public (ApiErrorDescriptor Descriptor, ProblemDetails? Problem) Describe(HttpContext httpContext, Exception exception)
    {
        var descriptor = ApiExceptionClassifier.Classify(exception,
            httpContext.User.Identity?.IsAuthenticated == true);

        _logger.Log(descriptor.Level, exception, "Exception in {Path}", httpContext.Request.Path);

        if (descriptor.RetryAfterSeconds is { } retryAfter)
            httpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

        var problem = descriptor.StatusCode == StatusCodes.Status408RequestTimeout
            ? null
            : Create(httpContext, descriptor.StatusCode, descriptor.Message, descriptor.NoRetry);
        return (descriptor, problem);
    }

    /// <summary>A ProblemDetails body for <paramref name="statusCode"/> with <paramref name="detail"/>.</summary>
    public ProblemDetails Create(HttpContext httpContext, int statusCode, string detail, bool noRetry)
    {
        var problem = _factory.CreateProblemDetails(httpContext, statusCode, detail: detail);
        problem.Extensions[NoRetryMember] = noRetry;
        return problem;
    }

    /// <summary>
    /// The 400 body of an invalid model: <c>errors</c> per field, and <c>detail</c> with the field messages
    /// joined by <c>|</c> (what the Hub shows, and what the former <c>error</c> member carried).
    /// </summary>
    public ValidationProblemDetails CreateValidation(HttpContext httpContext, ModelStateDictionary modelState)
    {
        var problem = _factory.CreateValidationProblemDetails(httpContext, modelState,
            StatusCodes.Status400BadRequest, detail: JoinMessages(modelState));
        problem.Extensions[NoRetryMember] = false;
        return problem;
    }

    /// <summary>
    /// The MVC result of <paramref name="problem"/>: its status and <c>application/problem+json</c>. Not an
    /// <see cref="ObjectResult"/> itself, so a controller's <c>[Produces("application/json")]</c> (a result
    /// filter) cannot rewrite the media type of an error body.
    /// </summary>
    public static IActionResult ToActionResult(ProblemDetails problem) => new ProblemResult(problem);

    private sealed class ProblemResult(ProblemDetails problem) : IActionResult, IStatusCodeActionResult
    {
        public int? StatusCode => problem.Status;

        public Task ExecuteResultAsync(ActionContext context) =>
            new ObjectResult(problem)
            {
                StatusCode = problem.Status,
                ContentTypes = { ProblemJsonContentType }
            }.ExecuteResultAsync(context);
    }

    private static string JoinMessages(ModelStateDictionary modelState)
    {
        var messages = modelState.Values
            .SelectMany(entry => entry.Errors)
            .Select(error => error.ErrorMessage)
            .Where(message => !string.IsNullOrEmpty(message))
            .ToList();
        return messages.Count == 0 ? InvalidRequestMessage : string.Join("|", messages);
    }
}
