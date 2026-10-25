using System.Reflection;

namespace Library.Execution;

public static class ReleaseInfo
{
    public static string Version => typeof(ReleaseInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public const int Protocol = 1;
    public const int DeploymentProtocol = 1;
    public const int CatalogSchema = 6;
    public const int MinimumDeliverySchema = 6;
    public const string Mode = "catalog-only";
    public static object Describe() => new { version = Version, protocol = Protocol, deploymentProtocol = DeploymentProtocol, catalogSchema = CatalogSchema, mode = Mode, production = false, pairing = false };
}
