using System;
using System.Runtime.Serialization;
using Registry.Common;

namespace Registry.Web.Exceptions;

internal class QuotaExceededException : InvalidOperationException
{

    public QuotaExceededException()
    {
    }

    public QuotaExceededException(string message) : base(message)
    {
    }

    public QuotaExceededException(long userStorage, long? maxStorage) : base(
        $"Storage quota exceeded: {CommonUtils.GetBytesReadable(userStorage)} out of {(maxStorage == null ? "UNLIMITED" : CommonUtils.GetBytesReadable(maxStorage.Value))}")
    {
    }

    public QuotaExceededException(string message, Exception innerException) : base(message, innerException)
    {
    }

}