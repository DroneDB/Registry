using Microsoft.AspNetCore.Mvc;

namespace Registry.Web.Models;

/// <summary>
/// Base controller for Registry controllers. Since the ImproveParallelWrites phase D
/// unification, every controller exception is handled centrally by the global
/// <see cref="Registry.Web.Utilities.ApiExceptionFilter"/>, whose status/noRetry/message
/// rules live in <see cref="Registry.Web.Utilities.ApiExceptionClassifier"/>; per-action
/// error wrappers and the legacy in-controller result builders no longer exist.
/// </summary>
public class ControllerBaseEx : ControllerBase
{
}