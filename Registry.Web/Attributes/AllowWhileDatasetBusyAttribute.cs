using System;

namespace Registry.Web.Attributes;

/// <summary>
/// An action on a dataset that still answers while the dataset is busy (a task imports it or rescans its index): its
/// delete, which stops the task first, and what follows and stops the task (the task list, its status, log, result and
/// events, its cancel). Every other action on a dataset is refused while it is busy (<c>DatasetBusyFilter</c>, which
/// <c>DatasetBusyConvention</c> puts on the actions of a dataset that are not marked).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class AllowWhileDatasetBusyAttribute : Attribute;

/// <summary>
/// A map tile action that is not checked for a busy dataset at all: the hot path of every map, read-only, and kept
/// without the extra query of the check. Not an operation meant for a busy dataset, unlike
/// <see cref="AllowWhileDatasetBusyAttribute"/>: a busy dataset is not shown on the map in the first place.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SkipDatasetBusyCheckAttribute : AllowWhileDatasetBusyAttribute;
