using Microsoft.Extensions.Hosting;

namespace DataPerformanceLab;

public static class LabEnvironment
{
    public const string Testing = "Testing";

    public static bool IsLocalLab(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment(Testing);

    public static void EnsureLocalLab(IHostEnvironment environment)
    {
        if (!IsLocalLab(environment))
            throw new InvalidOperationException(
                $"Local lab requires Development or Testing (was '{environment.EnvironmentName}').");
    }
}
