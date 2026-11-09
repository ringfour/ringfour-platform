using System.Reflection;

namespace Ringfour.Api;

public static class ServiceVersion
{
    public static readonly string Value =
        typeof(ServiceVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "0.0.0";
}
