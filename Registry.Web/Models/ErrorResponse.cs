using System;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Registry.Web.Models
{
    public class ErrorResponse
    {
        public string Error { get; set; }

        public ErrorResponse(string error)
        {
            Error = error;
        }

        public ErrorResponse(ModelStateDictionary modelState)
        {
            Error = "";
            foreach (var keyModelStatePair in modelState)
            {
                var key = keyModelStatePair.Key;
                var errors = keyModelStatePair.Value.Errors;

                if (errors != null && errors.Count > 0)
                {
                    foreach (var error in errors)
                    {
                        if (!string.IsNullOrEmpty(error.ErrorMessage))
                        {
                            Error += error.ErrorMessage + "|";
                        }
                    }

                }
            }

            if (Error.EndsWith("|"))
            {
                Error = Error.Substring(0, Error.Length - 1);
            }
            if (Error == "") Error = "Invalid";
        }
    }
}