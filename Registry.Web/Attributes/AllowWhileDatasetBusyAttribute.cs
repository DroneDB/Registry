using System;

namespace Registry.Web.Attributes;

/// <summary>
/// An action on a dataset that still answers while the dataset is busy (a task imports it or rescans its index): its
/// delete, which stops the task first, and what follows and stops the task (the task list, its status, log, result and
/// events, its cancel). Every other action on a dataset is refused while it is busy (<c>DatasetBusyFilter</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AllowWhileDatasetBusyAttribute : Attribute;
