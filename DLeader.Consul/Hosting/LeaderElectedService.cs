using DLeader.Consul.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DLeader.Consul.Hosting;

/// <summary>
/// Base class for a background service whose work should run on exactly one instance.
/// </summary>
/// <remarks>
/// <para>
/// The acquire/hold/release loop is the same in every consumer of this library, so it
/// lives here rather than being copied. Derive from this and implement
/// <see cref="ExecuteAsLeaderAsync"/>; it is called once per leadership term, with a
/// token that is cancelled the moment the term ends.
/// </para>
/// <para>
/// This replaces the <c>OnLeadershipAcquired</c> / <c>OnLeadershipLost</c> events
/// removed in 2.0, and improves on them in the way that mattered: the events handed the
/// handler nothing to fence with, while
/// <see cref="ExecuteAsLeaderAsync"/> receives the lease and therefore its
/// <see cref="ILeadershipLease.FencingToken"/>.
/// </para>
/// <para>
/// It is a convenience, not a second implementation. It has no path to Consul of its
/// own — everything goes through <see cref="ILeadershipLeaseProvider"/>, so the
/// guarantees are exactly the ones documented there, with no separate set to reason
/// about.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class InvoiceCloser : LeaderElectedService
/// {
///     private readonly IInvoiceStore _store;
///
///     public InvoiceCloser(ILeadershipLeaseProvider leases, ILogger&lt;InvoiceCloser&gt; logger, IInvoiceStore store)
///         : base(leases, logger) => _store = store;
///
///     protected override async Task ExecuteAsLeaderAsync(ILeadershipLease lease, CancellationToken ct)
///     {
///         while (!ct.IsCancellationRequested)
///         {
///             // The fencing token travels with the write.
///             await _store.CloseBatchAsync(lease.FencingToken, ct);
///             await Task.Delay(TimeSpan.FromSeconds(5), ct);
///         }
///     }
/// }
/// </code>
/// </example>
public abstract class LeaderElectedService : BackgroundService
{
    private readonly ILeadershipLeaseProvider _leases;
    private readonly ILogger _logger;

    /// <summary>
    /// How long to wait before trying again after leader work throws. Acquisition itself
    /// does not poll — it waits on a blocking query.
    /// </summary>
    protected virtual TimeSpan RetryDelay => TimeSpan.FromSeconds(5);

    /// <param name="leases">Provides the leadership leases this service runs under.</param>
    /// <param name="logger">Logger for leadership transitions and failures.</param>
    protected LeaderElectedService(ILeadershipLeaseProvider leases, ILogger logger)
    {
        _leases = leases;
        _logger = logger;
    }

    /// <summary>
    /// Runs for the duration of one leadership term.
    /// </summary>
    /// <param name="lease">
    /// The held lease. Pass its <see cref="ILeadershipLease.FencingToken"/> to every
    /// side effect, and have the receiving resource reject anything carrying a lower
    /// value — without that, this class gives you an advisory lock and a race. See the
    /// remarks on <see cref="ILeadershipLease"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancelled when leadership is lost or the host is stopping. Honour it: work that
    /// outlives it is work running after another instance has taken over.
    /// </param>
    /// <remarks>
    /// Returning normally releases the lease and re-enters the acquisition loop, so a
    /// service that should lead for as long as it can needs to loop until the token is
    /// cancelled. Throwing is logged, the lease is released, and acquisition is retried
    /// after <see cref="RetryDelay"/>.
    /// </remarks>
    protected abstract Task ExecuteAsLeaderAsync(
        ILeadershipLease lease,
        CancellationToken cancellationToken);

    /// <summary>
    /// Called when a term ends, before the next acquisition attempt. Default does
    /// nothing.
    /// </summary>
    /// <param name="reason">
    /// Why the term ended.
    /// <see cref="LeadershipLostReason.LocalDeadlineExceeded"/> means this instance
    /// could not reach Consul rather than being told to stand down, which is worth
    /// treating differently from an orderly hand-off.
    /// </param>
    protected virtual Task OnLeadershipEndedAsync(LeadershipLostReason? reason) =>
        Task.CompletedTask;

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ILeadershipLease? lease = null;

            try
            {
                // Waits on a blocking query rather than polling, so this takes over the
                // moment the previous leader releases.
                lease = await _leases.AcquireLeadershipAsync(stoppingToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Leadership acquired with fencing token {FencingToken}", lease.FencingToken);

                // Work stops when leadership ends as well as when the host does. Losing
                // the lease has to stop the work, not merely be observable by it.
                using var work = CancellationTokenSource.CreateLinkedTokenSource(
                    lease.LostToken, stoppingToken);

                await ExecuteAsLeaderAsync(lease, work.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Leadership ended while work was in flight. Ordinary.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Leader work failed; releasing the lease and retrying");

                await DelayQuietlyAsync(RetryDelay, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                if (lease is not null)
                {
                    var reason = lease.LostReason;

                    await lease.DisposeAsync().ConfigureAwait(false);

                    try
                    {
                        await OnLeadershipEndedAsync(reason).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "OnLeadershipEndedAsync threw");
                    }
                }
            }
        }
    }

    private static async Task DelayQuietlyAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The loop condition handles it.
        }
    }
}
