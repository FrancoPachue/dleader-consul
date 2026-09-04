using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DLeader.Consul.Diagnostics;

/// <summary>
/// The names this library publishes telemetry under, and the instruments themselves.
/// </summary>
/// <remarks>
/// <para>
/// Subscribe with OpenTelemetry using the public name constants:
/// </para>
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(LeadershipTelemetry.ActivitySourceName))
///     .WithMetrics(m => m.AddMeter(LeadershipTelemetry.MeterName));
/// </code>
/// <para>
/// The instrument that matters operationally is
/// <c>dleader.consul.leadership.lost</c> and its <c>reason</c> tag. A leader that
/// stands down because Consul expired its session is a different incident from one that
/// stands down because its own local deadline passed: the first means Consul made a
/// decision, the second means this node could not reach Consul to hear one. Logs
/// carried that difference only in prose.
/// </para>
/// </remarks>
public static class LeadershipTelemetry
{
    /// <summary>Name of the <see cref="System.Diagnostics.ActivitySource"/> this library publishes spans on.</summary>
    public const string ActivitySourceName = "DLeader.Consul";

    /// <summary>Name of the <see cref="System.Diagnostics.Metrics.Meter"/> this library publishes instruments on.</summary>
    public const string MeterName = "DLeader.Consul";

    /// <summary>Version reported for both the activity source and the meter.</summary>
    public const string Version = "1.13.0";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);

    private static readonly Meter Meter = new(MeterName, Version);

    /// <summary>Acquisition attempts, tagged with the outcome.</summary>
    internal static readonly Counter<long> Acquisitions = Meter.CreateCounter<long>(
        "dleader.consul.leadership.acquisitions",
        unit: "{attempt}",
        description: "Leadership acquisition attempts, tagged by outcome (acquired, contended, failed).");

    /// <summary>Leadership losses, tagged with which detector fired.</summary>
    internal static readonly Counter<long> Losses = Meter.CreateCounter<long>(
        "dleader.consul.leadership.lost",
        unit: "{loss}",
        description: "Leadership losses, tagged by reason. The reason distinguishes a decision Consul made from one this node made without reaching Consul.");

    /// <summary>How long each leadership term lasted.</summary>
    internal static readonly Histogram<double> Tenure = Meter.CreateHistogram<double>(
        "dleader.consul.leadership.tenure",
        unit: "s",
        description: "Duration of each completed leadership term.");

    /// <summary>1 while this process holds a lease, 0 otherwise.</summary>
    internal static readonly UpDownCounter<long> Held = Meter.CreateUpDownCounter<long>(
        "dleader.consul.leadership.held",
        unit: "{lease}",
        description: "Leases currently held by this process. Summed across a fleet it should never exceed one per service.");

    /// <summary>Session renewal attempts, tagged with the outcome.</summary>
    internal static readonly Counter<long> Renewals = Meter.CreateCounter<long>(
        "dleader.consul.session.renewals",
        unit: "{renewal}",
        description: "Session renewal attempts, tagged by outcome (ok, failed, expired).");

    /// <summary>
    /// Fencing token of the most recent lease this process acquired, per service.
    /// </summary>
    /// <remarks>
    /// Useful as a monotonicity check: this value going backwards for a service means
    /// the assumption fencing rests on has been violated, which in practice means the
    /// Consul cluster was restored from a snapshot or rebuilt.
    /// </remarks>
    internal static readonly Counter<long> FencingTokenIssued = Meter.CreateCounter<long>(
        "dleader.consul.leadership.fencing_token_issued",
        unit: "{token}",
        description: "Fencing tokens issued to this process.");
}
