using System;
using System.Runtime.Serialization;

namespace Registry.Web.Services.Adapters
{
    [Serializable]
    internal class MaxUserStorageException : InvalidOperationException
    {
        private long userStorage;
        private long maxStorage;

        public MaxUserStorageException()
        {
        }

        public MaxUserStorageException(string message) : base(message)
        {
        }

        public MaxUserStorageException(long userStorage, long maxStorage)
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