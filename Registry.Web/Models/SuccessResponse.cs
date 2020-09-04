using System;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Registry.Web.Models
{
    public class SuccessResponse
    {
        public bool Success { get; }

        public SuccessResponse()
        {
            Success = true; // Always
        }
    }
}