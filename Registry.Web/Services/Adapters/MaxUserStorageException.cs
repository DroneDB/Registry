using System;
using System.Runtime.Serialization;
using Registry.Common;

namespace Registry.Web.Services.Adapters
{
    [Serializable]
    internal class MaxUserStorageException : InvalidOperationException
    {
        private long userStorage;
        private long? maxStorage;

        public MaxUserStorageException()
        {
        }

        public MaxUserStorageException(string message) : base(message)
        {
        }

        public MaxUserStorageException(long userStorage, long? maxStorage) : base(
            $"User run out of space: usage {CommonUtils.GetBytesReadable(userStorage)} out of {(maxStorage == null ? "UNLIMITED" : CommonUtils.GetBytesReadable(maxStorage.Value))}")
        {
            this.userStorage = userStorage;
            this.maxStorage = maxStorage;
        }

        public MaxUserStorageException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected MaxUserStorageException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}