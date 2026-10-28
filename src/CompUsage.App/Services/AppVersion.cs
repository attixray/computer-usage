using System.Reflection;

namespace CompUsage.App.Services;

public static class AppVersion
{
    // Read the loaded application assembly, so an older running monitor reports its own version after an upgrade.
    public static string Current { get; } = typeof(AppVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}
