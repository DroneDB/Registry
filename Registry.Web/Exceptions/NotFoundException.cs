using System;

namespace Registry.Web.Exceptions;

public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message)
    {
        //
    }
    
    public NotFoundException(string message, Exception inner) : base(message, inner)
    {}
}