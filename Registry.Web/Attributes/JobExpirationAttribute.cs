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
        context.JobExpirationTimeout = TimeSpan.FromMinutes(ExpirationTimeoutInMinutes);
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        //
    }
}