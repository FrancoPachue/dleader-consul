# DLeader.Consul

A .NET library that provides distributed leader election capabilities using HashiCorp Consul. This library helps you implement leader election patterns in distributed systems with a clean and simple API.

## Features

- 🔄 Automatic leader election using Consul
- 🔌 Easy service registration and discovery
- 🏃 Automatic session management and renewal
- 🎯 Event-driven leadership changes
- ⚡ High performance and low overhead
- 🛠️ Built with dependency injection in mind
- 📝 Extensive logging and diagnostics

## Installation

```bash
dotnet add package DLeader.Consul
```

## Quick Start

```csharp
// Register the service
services.AddConsulLeader(options =>
{
    options.ServiceName = "my-service";
    options.Address = "http://consul:8500";
});

// Use in your service
public class MyService : BackgroundService
{
    private readonly ILeaderElection _leaderElection;
    
    public MyService(ILeaderElection leaderElection)
    {
        _leaderElection = leaderElection;
        _leaderElection.OnLeadershipAcquired += HandleLeadershipAcquired;
        _leaderElection.OnLeadershipLost += HandleLeadershipLost;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _leaderElection.StartLeaderElectionAsync(stoppingToken);
    }
}
```

## Configuration Options

```csharp
public class ConsulOptions
{
    public string ServiceName { get; set; } = "leadership-service";
    public string Address { get; set; } = "http://localhost:8500";
    public int SessionTTL { get; set; } = 15;
    public int RenewInterval { get; set; } = 5;
    public int LeaderCheckInterval { get; set; } = 1;
}
```

## Advanced Usage

### Custom Service Registration

```csharp
services.AddConsulLeader(options =>
{
    options.ServiceName = "my-service";
    options.Address = "http://consul:8500";
    options.SessionTTL = 15;
    options.RenewInterval = 5;
}, serviceOptions =>
{
    serviceOptions.ServicePort = 5000;
    serviceOptions.HealthCheckEndpoint = "/health";
    serviceOptions.Tags = new[] { "production", "web" };
});
```


## Prerequisites

- .NET 8.0 or higher
- Consul server (local or remote)

## Dependencies

- Consul (>= 1.6.10.9)
- Microsoft.Extensions.DependencyInjection (>= 8.0.0)
- Microsoft.Extensions.Logging (>= 8.0.0)

## Running the Examples

1. Start Consul:
```bash
docker run -d -p 8500:8500 consul:latest
```

2. Run multiple instances:
```bash
dotnet run --project samples/ConsulLeaderExample
```
--

You can run 
```bash
docker compose up --build
```
## Testing

```bash
dotnet test
```

## Contributing

1. Fork the repository
2. Create a feature branch
3. Commit your changes
4. Push to the branch
5. Create a Pull Request

## License

This project is licensed under the MIT License - see the LICENSE file for details

## Support

If you need help or have any questions:
- Open an issue
- Submit a pull request
- Contact the maintainers

## Maintainers

- Franco Pachue

## Acknowledgments

- HashiCorp Consul team
- .NET community

## Tags

`consul`, `leader-election`, `distributed-systems`, `dotnet`, `csharp`, `microservices`, `service-discovery`
