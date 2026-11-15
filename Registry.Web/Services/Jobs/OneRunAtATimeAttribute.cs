using System;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// Marks a job class whose job methods must not overlap, on this host or any other: <see cref="OneRunAtATimeFilter"/>
/// lets one run of each job method at a time and ends the others without running them.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class OneRunAtATimeAttribute : Attribute;
