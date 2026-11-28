using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Registry.Web.Services.Users;

namespace Registry.Web.Filters;

/// <summary>
/// Counts a request as one sign-in attempt of its client address (<see cref="SignInThrottle.CountAttempt"/>) before
/// the action runs, so attempts beyond the limit are refused with 429 and <c>Retry-After</c> whatever the account.
/// Declared with <see cref="CountSignInAttemptAttribute"/>.
/// </summary>
public class SignInAttemptFilter : IResourceFilter
{
    private readonly SignInThrottle _throttle;

    public SignInAttemptFilter(SignInThrottle throttle)
    {
        _throttle = throttle;
    }

    public void OnResourceExecuting(ResourceExecutingContext context) => _throttle.CountAttempt(context.HttpContext);

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
        // Nothing to do after the action
    }
}

/// <summary>Counts every request to the action as a sign-in attempt of its client address.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CountSignInAttemptAttribute : ServiceFilterAttribute
{
    public CountSignInAttemptAttribute() : base(typeof(SignInAttemptFilter))
    {
    }
}
