using System;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;

namespace Registry.Web.Attributes;

public class JobExpirationAttribute : JobFilterAttribute, IApplyStateFilter
{
    public int ExpirationTimeoutInMinutes { get; set; } = 5;

    public JobExpirationAttribute()
    {

    }

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // Preserve failed jobs at the global retention so their history and
        // captured logs remain available for diagnostics. Only the short-lived,
        // high-frequency success records are expired quickly to curb churn.
        if (context.NewState is FailedState)
            return;

        context.JobExpirationTimeout = TimeSpan.FromMinutes(ExpirationTimeoutInMinutes);
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        //
    }
}