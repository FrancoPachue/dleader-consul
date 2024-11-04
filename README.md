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

View package on [NuGet Gallery](https://www.nuget.org/packages/DLeader.Consul)

## Quick Start

```csharp
// Register the service
services.AddConsulLeaderElection(options =>
{
    options.ServiceName = "my-service";
    options.Address = "http://consul:8500";
});

// Use in your service
public class MyService : BackgroundService
{
    private readonly ILeaderElection _leaderElection;
    private readonly ILogger<MyService> _logger;
    
    public MyService(ILeaderElection leaderElection, ILogger<MyService> logger)
    {
        _leaderElection = leaderElection;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _leaderElection.StartLeaderElectionAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (await _leaderElection.IsLeaderAsync())
            {
                _logger.LogInformation("This instance is the leader");
                // Do leader-specific work
            }

            await Task.Delay(1000, stoppingToken);
        }
    }
}
```

## Configuration Options

### ConsulOptions

```csharp
public class ConsulOptions
{
    public string ServiceName { get; set; } = string.Empty;
    public string Address { get; set; } = "http://localhost:8500";
    public int SessionTTL { get; set; } = 10;
    public int LeaderCheckInterval { get; set; } = 5;
    public int RenewInterval { get; set; } = 5;
    public int VerificationRetries { get; set; } = 3;
    public int VerificationRetryDelay { get; set; } = 1;
}
```

### ServiceRegistrationOptions

```csharp
public class ServiceRegistrationOptions
{
    public int ServicePort { get; set; }
    public string HealthCheckEndpoint { get; set; } = "/health";
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan DeregisterAfter { get; set; } = TimeSpan.FromMinutes(1);
}
```

## Advanced Usage

### Configuration in appsettings.json

```json
{
  "Consul": {
    "ServiceName": "your-service-name",
    "Address": "http://localhost:8500",
    "SessionTTL": 10,
    "LeaderCheckInterval": 5,
    "RenewInterval": 5,
    "VerificationRetries": 3,
    "VerificationRetryDelay": 1
  },
  "ServiceRegistration": {
    "ServicePort": 5000,
    "HealthCheckEndpoint": "/health",
    "HealthCheckInterval": "00:00:10",
    "HealthCheckTimeout": "00:00:05",
    "DeregisterAfter": "00:01:00"
  }
}
```

## Prerequisites

- .NET 8.0
- Consul server (local or remote)

## Running the Examples

1. Start Consul:
```bash
docker-compose up -d
```

2. Run multiple instances:
```bash
dotnet run --project DLeader.Consul.Example
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

- Franco Pacheco

## Acknowledgments

- HashiCorp Consul team
- .NET community

## Tags

`consul`, `leader-election`, `distributed-systems`, `dotnet`, `csharp`, `microservices`, `service-discovery`