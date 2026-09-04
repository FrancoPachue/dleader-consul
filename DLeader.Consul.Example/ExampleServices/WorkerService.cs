using DLeader.Consul.Abstractions;

namespace DLeader.Consul.Example.Services
{
    /// <summary>
    /// The canonical shape of a leader-only background worker.
    /// </summary>
    /// <remarks>
    /// The loop has two states and no third. Either this instance holds a lease, in
    /// which case it does leader work under a token that is cancelled the moment the
    /// lease is lost, or it does not, in which case it waits and tries again. There is
    /// no "am I the leader?" question anywhere, because the answer would be stale
    /// before the next line ran.
    /// </remarks>
    public class WorkerService : BackgroundService
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan WorkInterval = TimeSpan.FromSeconds(1);

        private readonly ILeadershipLeaseProvider _leases;
        private readonly IMessageBroker _messageBroker;
        private readonly ILogger<WorkerService> _logger;
        private bool _isSubscribed;

        public WorkerService(
            ILeadershipLeaseProvider leases,
            IMessageBroker messageBroker,
            ILogger<WorkerService> logger)
        {
            _leases = leases;
            _messageBroker = messageBroker;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await using var lease = await _leases.TryAcquireLeadershipAsync(stoppingToken);

                if (lease is null)
                {
                    await DoFollowerWorkAsync();
                    await DelayAsync(RetryDelay, stoppingToken);
                    continue;
                }

                await RunAsLeaderAsync(lease, stoppingToken);
            }
        }

        private async Task RunAsLeaderAsync(ILeadershipLease lease, CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "Leadership acquired with fencing token {FencingToken}", lease.FencingToken);

            // Work stops when leadership is lost as well as when the host shuts down, so
            // the leader-only work never outlives the claim it depends on.
            using var work = CancellationTokenSource.CreateLinkedTokenSource(
                lease.LostToken, stoppingToken);

            try
            {
                while (!work.IsCancellationRequested)
                {
                    await DoLeaderWorkAsync(lease, work.Token);
                    await Task.Delay(WorkInterval, work.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Leadership moved on, or the host is stopping.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Leader work failed; releasing the lease");
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Leadership lost; returning to follower work");
            }
        }

        private async Task DoLeaderWorkAsync(ILeadershipLease lease, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Doing leader work...");

            // Every side effect carries the fencing token. A downstream resource that
            // records the highest token it has accepted can then reject this write if a
            // later leader has already been there - which is the only thing that makes
            // the exclusion actually safe.
            await _messageBroker.BroadcastAsync(
                "leader-updates",
                $"{lease.FencingToken}:Leader is working");

            cancellationToken.ThrowIfCancellationRequested();
        }

        private async Task DoFollowerWorkAsync()
        {
            if (_isSubscribed)
            {
                return;
            }

            _logger.LogInformation("Subscribing to leader updates...");
            await _messageBroker.SubscribeAsync("leader-updates", HandleLeaderChange);
            _isSubscribed = true;
        }

        private Task HandleLeaderChange(string message)
        {
            _logger.LogInformation("Received leader change message: {Message}", message);
            return Task.CompletedTask;
        }

        private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The caller's loop condition handles it.
            }
        }
    }
}
