using Microsoft.AspNetCore.Diagnostics;

namespace DataPerformanceLab.Api.Errors;

public sealed class LabExceptionHandler(ILogger<LabExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        if (exception is OperationCanceledException)
        {
            return false;
        }

        logger.LogError(exception, "Unhandled exception on {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(
            ProblemDetailsWriter.Create(httpContext, StatusCodes.Status500InternalServerError, "UnexpectedError", "Unexpected server error"),
            ct);

        return true;
    }
}
