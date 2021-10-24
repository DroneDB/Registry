using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Registry.Web.Controllers;

namespace Registry.Web.Attributes
{
    public class RestrictToS3Attribute : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var controller = (S3BridgeController)context.Controller;
            if (!controller.IsS3Enabled())
            {
                context.Result = new NotFoundResult();
                return;
            }
            base.OnActionExecuting(context);
        }
    }
}