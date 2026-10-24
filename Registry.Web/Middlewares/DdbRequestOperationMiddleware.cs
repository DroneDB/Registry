#nullable enable
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Middlewares;

/// <summary>
/// Gives every request a libddb operation (XI-08): the native calls made while it is served, on whatever thread, log
/// with an id that <see cref="RequestOperations"/> maps to the request id, the same one Serilog writes as
/// <c>RequestId</c> on the request's own records. The one place a request's operation is set: the code that calls
/// libddb passes nothing. A job keeps its own (<see cref="Services.Jobs.JobOperations"/>), which it enters in the
/// job's own flow.
/// </summary>
public sealed class DdbRequestOperationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using var operation = RequestOperations.Begin(context.TraceIdentifier);
        await next(context);
    }
}

public static class DdbRequestOperationExtensions
{
    /// <summary>Runs every later middleware and endpoint under the request's libddb operation.</summary>
    public static IApplicationBuilder UseDdbRequestOperation(this IApplicationBuilder app) =>
        app.UseMiddleware<DdbRequestOperationMiddleware>();
}
