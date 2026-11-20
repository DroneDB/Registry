using System;

namespace Registry.Ports.DroneDB;

/// <summary>
/// libddb could not read a file as the type its name or content says it is (a <c>.laz</c> that is not
/// LAZ, a raster GDAL cannot open, malformed JSON): the file is at fault, not the server, so the same
/// file can never be indexed and the HTTP API answers 400 with <c>noRetry</c>.
/// </summary>
public class DdbInvalidContentException : DdbException
{
    public DdbInvalidContentException()
    {
    }

    public DdbInvalidContentException(string message) : base(message)
    {
    }

    public DdbInvalidContentException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
