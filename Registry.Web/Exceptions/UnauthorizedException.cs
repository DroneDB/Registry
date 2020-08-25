using System;

namespace Registry.Web.Exceptions
{
    public class UnauthorizedException : Exception
    {
        public UnauthorizedException(string message) : base(message)
        {
            //
        }
    }
}