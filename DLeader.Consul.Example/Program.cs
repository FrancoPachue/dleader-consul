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

builder.Services.AddConsulLeadership(options =>
{
    options.ServiceName = builder.Configuration["ConsulConfig:ServiceName"] ?? "dleader-consul-example";
    options.Address = builder.Configuration["ConsulConfig:Address"] ?? consulAddress;
    options.SessionTTL = builder.Configuration.GetValue<int>("ConsulConfig:SessionTTL", 10);
    options.RenewInterval = builder.Configuration.GetValue<int>("ConsulConfig:RetryInterval", 5);
    options.LeaderCheckInterval = builder.Configuration.GetValue<int>("ConsulConfig:LeaderCheckInterval", 5);
    options.VerificationRetries = builder.Configuration.GetValue<int>("ConsulConfig:VerificationRetries", 3);
    options.VerificationRetryDelay = builder.Configuration.GetValue<int>("ConsulConfig:VerificationRetryDelay", 1);
}, serviceOptions =>
{
    serviceOptions.ServicePort = 8080;
});

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