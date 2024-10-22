using System;
using System.Runtime.Serialization;
using Registry.Common;

namespace Registry.Web.Exceptions;

[Serializable]
internal class QuotaExceededException : InvalidOperationException
{
    private long userStorage;
    private long? maxStorage;

    public QuotaExceededException()
    {
    }

    public QuotaExceededException(string message) : base(message)
    {
    }

    public QuotaExceededException(long userStorage, long? maxStorage) : base(
        $"Storage quota exceeded: {CommonUtils.GetBytesReadable(userStorage)} out of {(maxStorage == null ? "UNLIMITED" : CommonUtils.GetBytesReadable(maxStorage.Value))}")
    {
        this.userStorage = userStorage;
        this.maxStorage = maxStorage;
    }

    public QuotaExceededException(string message, Exception innerException) : base(message, innerException)
    {
    }

    protected QuotaExceededException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }
}