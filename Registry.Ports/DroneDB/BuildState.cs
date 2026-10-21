using System.Runtime.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>The build state of an entry (ddb.h "build manifest", XI-04), in libddb's precedence order.</summary>
public enum BuildState
{
    /// <summary>Not built, failed or incomplete (and every entry that is not buildable).</summary>
    [EnumMember(Value = "none")] None,

    /// <summary>A build waits for missing dependencies (a pending marker).</summary>
    [EnumMember(Value = "pending")] Pending,

    /// <summary>A live build holds the build lock of the entry.</summary>
    [EnumMember(Value = "building")] Building,

    /// <summary>The artifacts are complete.</summary>
    [EnumMember(Value = "complete")] Complete
}
