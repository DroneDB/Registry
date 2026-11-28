namespace Registry.Web.Services.Jobs;

/// <summary>
/// The backoff chain of the dataset jobs: how many times a job is retried and the seconds before each retry. An
/// attribute argument cannot read an array, so the delays are constants, defined once for every
/// <c>AutomaticRetry</c> of a dataset job (<see cref="DdbRetriedJob"/>, the build jobs of <see cref="DdbJobs"/> and
/// the build job stored by earlier releases).
/// </summary>
public static class DdbRetryPolicy
{
    public const int Attempts = 5;
    public const int Delay1 = 15;
    public const int Delay2 = 60;
    public const int Delay3 = 180;
    public const int Delay4 = 600;
    public const int Delay5 = 1800;
}
