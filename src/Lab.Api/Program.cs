using DataPerformanceLab;
using DataPerformanceLab.Api.Endpoints;
using DataPerformanceLab.Api.Errors;

var builder = WebApplication.CreateBuilder(args);

LabEnvironment.EnsureLocalLab(builder.Environment);

builder.Services.AddLabCore(builder.Configuration);
builder.Services.AddExceptionHandler<LabExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

app.MapProductEndpoints();
app.MapHealthEndpoints();
app.MapCacheMetricsEndpoints();

app.Run();

// The API never migrates or seeds on startup; that is the CLI's job.
public partial class Program { }
