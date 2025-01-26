public class ServiceConfiguration
{
    public string Name { get; set; } = default!;
    public string Path { get; set; } = default!;
    public string BaseUrl { get; set; } = default!;
    public List<EndpointConfiguration> Endpoints { get; set; } = new();
}

public class EndpointConfiguration
{
    public string Path { get; set; } = default!;
    public string RelativePath { get; set; } = default!;
    public List<string> AllowedMethods { get; set; } = new();
}

public class GatewayConfiguration
{
    public List<ServiceConfiguration> Services { get; set; } = new();
} 