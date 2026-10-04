using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace DataPerformanceLab.Api.Errors;

public static class ProblemDetailsWriter
{
    public static ProblemDetails Create(HttpContext httpContext, int statusCode, string code, string title, string? detail = null)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        return problem;
    }
}
