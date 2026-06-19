using System;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// Base exception for DroneDB native errors.
/// </summary>
public class DdbException : Exception
{
    public DdbException()
    {

    }

    public DdbException(string message) : base(message)
    {

    }

    public DdbException(string message, Exception innerException) : base(message, innerException)
    {

    }

}