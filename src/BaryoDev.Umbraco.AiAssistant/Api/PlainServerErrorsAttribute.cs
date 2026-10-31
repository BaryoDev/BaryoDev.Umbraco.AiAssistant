using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BaryoDev.Umbraco.AiAssistant.Api;

/// <summary>
/// Turns an unexpected exception into a plain message. The detail goes to the server log, never to the browser.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PlainServerErrorsAttribute : ExceptionFilterAttribute
{
    public const string Message = "Something went wrong. The details are in the server log.";

    public override void OnException(ExceptionContext context)
    {
        // The caller went away. There is nobody to answer and nothing to log as an error.
        if (context.Exception is OperationCanceledException && context.HttpContext?.RequestAborted.IsCancellationRequested == true) return;
        try
        {
            var factory = context.HttpContext?.RequestServices?.GetService<ILoggerFactory>();
            factory?.CreateLogger("BaryoDev.Umbraco.AiAssistant").LogError(context.Exception, "AI Assistant request failed");
        }
        catch
        {
            // Logging must never stop the plain message from going out.
        }
        context.Result = new ObjectResult(new ProblemDetails { Status = 500, Title = "Error", Detail = Message }) { StatusCode = StatusCodes.Status500InternalServerError };
        context.ExceptionHandled = true;
    }
}
