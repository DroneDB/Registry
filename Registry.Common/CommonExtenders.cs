using System.Collections.Generic;
using System.IO;

namespace Registry.Common;

public static class CommonExtenders
{
    /// <summary>
    /// Safe reset to the beginning
    /// </summary>
    /// <param name="stream"></param>
    public static void SafeReset(this Stream stream)
    {
        if (stream.CanSeek)
            stream.Seek(0, SeekOrigin.Begin);
    }

    /// <summary>
    /// True when both dictionaries hold the same values (<see cref="PlainValue"/>): the lists and dictionaries inside are
    /// compared by content, not by reference.
    /// </summary>
    public static bool IsSameAs(this Dictionary<string, object> a, Dictionary<string, object> b) =>
        PlainValue.AreEqual(a, b);
}