using DLeader.Consul.Messaging;
using DLeader.Consul.Example;
using DLeader.Consul.Example.Services;
using DLeader.Consul.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

var consulAddress = builder.Environment.IsDevelopment()
    ? "http://localhost:8500"  
    : "http://consul:8500";  

// Leadership and messaging are separate packages in 2.0. Both register the Consul
// client with TryAddSingleton, so calling them together shares one connection.
builder.Services.AddConsulLeaderElection(options =>
{
    options.ServiceName = builder.Configuration["ConsulConfig:ServiceName"] ?? "dleader-consul-example";
    options.Address = builder.Configuration["ConsulConfig:Address"] ?? consulAddress;
    options.SessionTTL = builder.Configuration.GetValue<int>("ConsulConfig:SessionTTL", 10);
    options.LockDelaySeconds = builder.Configuration.GetValue<int>("ConsulConfig:LockDelaySeconds", 15);
    options.LeaseSafetyMarginSeconds =
        builder.Configuration.GetValue<int>("ConsulConfig:LeaseSafetyMarginSeconds", 2);
    options.AclToken = builder.Configuration["ConsulConfig:AclToken"] ?? string.Empty;
});

builder.Services.AddConsulMessaging();

//builder.Services.AddDistributedCache(options =>
//{
//    options.MaxCacheSize = 1000;
//    options.DefaultTTL = TimeSpan.FromMinutes(5);
//    options.CleanupInterval = TimeSpan.FromMinutes(1);
//});

builder.Services.AddHostedService<ScheduledTasksService>();

builder.Services.AddControllers();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseRouting();

app.MapControllers();
app.MapHealthChecks("/health");

await app.RunAsync();