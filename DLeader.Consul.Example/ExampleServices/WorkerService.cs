using DLeader.Consul.Abstractions;

namespace DLeader.Consul.Example.Services
{
    public class WorkerService : BackgroundService
    {
        private readonly ILeaderElection _leaderElection;
        private readonly IMessageBroker _messageBroker;
        private readonly ILogger<WorkerService> _logger;
        private bool _isSubscribed = false;

        public WorkerService(
            ILeaderElection leaderElection,
            IMessageBroker messageBroker,
            ILogger<WorkerService> logger)
        {
            _leaderElection = leaderElection;
            _logger = logger;
            _messageBroker = messageBroker;
            _leaderElection.OnLeadershipAcquired += HandleLeadershipAcquired;
            _leaderElection.OnLeadershipLost += HandleLeadershipLost;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // Iniciar el proceso de elección
                await _leaderElection.StartLeaderElectionAsync(stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    if (await _leaderElection.IsLeaderAsync())
                    {
                        await DoLeaderWork(stoppingToken);
                    }
                    else
                    {
                        await DoFollowerWork(stoppingToken);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in worker service");
                throw;
            }
        }

        private async Task HandleLeadershipAcquired()
        {
            _logger.LogInformation("Leadership acquired!");
            // Inicializar recursos de líder si es necesario
        }

        private async Task HandleLeadershipLost()
        {
            _logger.LogInformation("Leadership lost!");
            // Limpiar recursos de líder si es necesario
        }

        private async Task DoLeaderWork(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Doing leader work...");
            await _messageBroker.BroadcastAsync("leader-updates", "Leader is working");
        }

        private async Task DoFollowerWork(CancellationToken cancellationToken)
        {
            if (!_isSubscribed)
            {
                _logger.LogInformation("Subscribing to leader updates...");
                await _messageBroker.SubscribeAsync("leader-updates", HandleLeaderChange);
                _isSubscribed = true;
            }
        }

        private async Task HandleLeaderChange(string message)
        {
            _logger.LogInformation("Received leader change message: {Message}", message);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _leaderElection.OnLeadershipAcquired -= HandleLeadershipAcquired;
            _leaderElection.OnLeadershipLost -= HandleLeadershipLost;

            await base.StopAsync(cancellationToken);
        }
    }
}
