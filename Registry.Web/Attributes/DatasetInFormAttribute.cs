using System;

namespace Registry.Web.Attributes;

/// <summary>
/// An action that names a further dataset in its form, besides the one of its route (the destination of a transfer):
/// the busy-dataset gate (<c>DatasetBusyFilter</c>) refuses the request while that dataset is busy too, as it refuses a
/// request on its route.
/// </summary>
/// <param name="organizationField">The form field that holds the organization slug.</param>
/// <param name="datasetField">The form field that holds the dataset slug.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class DatasetInFormAttribute(string organizationField, string datasetField) : Attribute
{
    /// <summary>The form field that holds the organization slug.</summary>
    public string OrganizationField { get; } = organizationField;

    /// <summary>The form field that holds the dataset slug.</summary>
    public string DatasetField { get; } = datasetField;
}
