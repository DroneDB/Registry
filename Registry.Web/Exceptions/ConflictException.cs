using System;

namespace Registry.Web.Exceptions
{
    public class ConflictException : Exception
    {
        public ConflictException(string message) : base(message)
        {
            //
        }
    }
}