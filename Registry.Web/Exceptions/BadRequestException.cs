using System;

namespace Registry.Web.Exceptions
{
    public class BadRequestException : Exception
    {
        public BadRequestException(string message) : base(message)
        {
            //
        }
    }
}