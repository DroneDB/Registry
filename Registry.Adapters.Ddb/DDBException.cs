using System;

namespace Registry.Adapters.Ddb
{
    public class DDBException : Exception
    {
        public DDBException()
        {

        }
        
        public DDBException(string message) : base(message)
        {

        }

        public DDBException(string message, Exception innerException) : base(message, innerException)
        {

        }

    }
}
