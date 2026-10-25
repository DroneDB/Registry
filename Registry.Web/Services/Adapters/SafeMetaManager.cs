using System;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// The well-known meta of a dataset (name, visibility, entries, ...) by name: null when a value is not set.
/// </summary>
public class SafeMetaManager
{
    public const string NameField = "name";
    public const string LastUpdateField = "mtime";
    public const string PublicField = "public";
    public const string VisibilityField = "visibility";
    public const string ObjectsCountField = "entries";

    private readonly IMetaManager _manager;

    public SafeMetaManager(IMetaManager manager)
    {
        _manager = manager;
    }

    /// <summary>
    /// The value <paramref name="read"/> returns, or null when libddb has none. A transient error (busy index,
    /// build in progress, full disk, shutdown: <see cref="DdbErrors.IsTransient"/>) says nothing about the value
    /// and propagates.
    /// </summary>
    private static T Read<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (DdbException ex) when (!DdbErrors.IsTransient(ex))
        {
            return default;
        }
    }

    public string Name
    {
        get => Read(() => _manager.Get<string>(NameField));
        set
        {
            if (value == null)
                throw new ArgumentException("Name cannot be null");

            _manager.Set(NameField, value);
        }
    }

    public Visibility? Visibility
    {
        get => Read(() => (Visibility?)_manager.Get<int>(VisibilityField));
        set
        {
            if (value == null)
                throw new ArgumentException("Visibility cannot be null");
            _manager.Set(VisibilityField, ((int)value).ToString());
        }
    }

    public bool? IsPublic
    {
        get => Read(() => (bool?)_manager.Get<bool>(PublicField));
        set
        {
            if (value == null) throw new InvalidOperationException("Cannot set null value");
            _manager.Set(PublicField, value.Value ? "1" : "0");
        }
    }

    public const string TaglineField = "tagline";
    public const int TaglineMaxLength = 256;

    public string Tagline
    {
        get => Read(() => _manager.Get<string>(TaglineField));
        set
        {
            if (value == null)
            {
                _manager.Unset(TaglineField);
            }
            else
            {
                if (value.Length > TaglineMaxLength)
                    throw new ArgumentException($"Tagline cannot exceed {TaglineMaxLength} characters");
                _manager.Set(TaglineField, value);
            }
        }
    }
}