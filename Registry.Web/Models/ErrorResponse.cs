using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Registry.Web.Models
{
    public class ErrorResponse
    {
        public string Error { get; set; }
        public bool NoRetry { get; set; }

        public ErrorResponse(string error, bool noRetry = false)
        {
            Error = error;
            NoRetry = noRetry;
        }

        public ErrorResponse(ModelStateDictionary modelState)
        {
            Error = "";
            foreach (var pair in modelState)
            {
                var key = pair.Key;
                var errors = pair.Value.Errors;

                if (!errors.Any()) continue;

                foreach (var error in errors)
                {
                    if (!string.IsNullOrEmpty(error.ErrorMessage))
                    {
                        Error += error.ErrorMessage + "|";
                    }
                }
            }

            if (Error.EndsWith("|"))
                Error = Error[..^1];

            if (Error == string.Empty)
                Error = "Invalid";
        }
    }
}