using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Registry.Common;

public static class CommonExtenders
{
    /// <summary>
    /// Safe reset to the beginning
    /// </summary>
    /// <param name="stream"></param>
    public static void Reset(this Stream stream)
    {
        if (!stream.CanSeek)
            throw new InvalidOperationException("Stream does not support seeking");

        stream.Seek(0, SeekOrigin.Begin);
    }

    /// <summary>
    /// Safe reset to the beginning
    /// </summary>
    /// <param name="stream"></param>
    public static void SafeReset(this Stream stream)
    {
        if (stream.CanSeek)
            stream.Seek(0, SeekOrigin.Begin);
    }

    public static bool IsSameAs(this Dictionary<string, object> a, Dictionary<string, object> b)
    {
        return a.Count == b.Count && !a.Except(b).Any();
    }

}