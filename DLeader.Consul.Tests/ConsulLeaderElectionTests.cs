using Xunit;
using Moq;
using Consul;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DLeader.Consul.Configuration;
using System.Text;
using DLeader.Consul.Implementations;
using DLeader.Consul.Exceptions;

namespace DLeader.Consul.Tests.Implementations
{
    /// <summary>
    /// What can be tested without a Consul.
    /// </summary>
    /// <remarks>
    /// This file used to be nine hundred lines, most of it exercising the campaign API
    /// that 2.0 removed. What is left is deliberately small: argument validation,
    /// disposal ownership, and the pure translation in GetCurrentLeaderAsync. Everything
    /// about acquisition, fencing tokens and loss detection lives in the integration
    /// suite, because that is the only place it can be tested honestly — mocks of
    /// Consul's failure modes are what let the wedged-session bug ship in the first
    /// place.
    /// </remarks>
    public class ConsulLeaderElectionTests
    {
        private readonly Mock<ILogger<ConsulLeaderElection>> _loggerMock = new();
        private readonly Mock<IConsulClient> _consulClientMock = new();
        private readonly Mock<IKVEndpoint> _kvEndpointMock = new();
        private readonly ConsulOptions _consulOptions;
        private readonly string _lockKey;

        public ConsulLeaderElectionTests()
        {
            _consulOptions = new ConsulOptions
            {
                ServiceName = "test-service",
                Address = "http://localhost:8500",
                SessionTTL = 10
            };

            _lockKey = $"service/{_consulOptions.ServiceName}/leader";
            _consulClientMock.Setup(x => x.KV).Returns(_kvEndpointMock.Object);
        }

        private ConsulLeaderElection Create() =>
            new(_loggerMock.Object, Options.Create(_consulOptions), _consulClientMock.Object);

        [Fact]
        public void InstanceId_IsReadable_AndUniquePerInstance()
        {
            using var first = Create();
            using var second = Create();

            // Readable, because it names the Consul sessions this instance creates and
            // someone will read it in a log.
            Assert.StartsWith("test-service-", first.InstanceId);
            Assert.Contains($"-{Environment.ProcessId}-", first.InstanceId);

            // Unique, for the same reason: two instances sharing an id would be
            // indistinguishable in Consul's session list.
            Assert.NotEqual(first.InstanceId, second.InstanceId);
        }

        [Theory]
        [InlineData(0)]      // not positive
        [InlineData(-1)]
        [InlineData(10)]     // equal to the TTL leaves no margin
        [InlineData(30)]     // larger than the TTL is nonsense
        public async Task TryAcquireLeadershipAsync_RejectsAnUnusableSafetyMargin(int margin)
        {
            // A margin at or above the TTL would mean the lease declares itself lost
            // before it has had any chance to renew, so it can never hold leadership.
            // Failing loudly beats a lease that mysteriously never survives.
            _consulOptions.LeaseSafetyMarginSeconds = margin;
            using var election = Create();

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => election.TryAcquireLeadershipAsync());
        }

        [Fact]
        public async Task GetCurrentLeaderAsync_ReturnsTheHolder_WhenTheKeyIsHeld()
        {
            _kvEndpointMock
                .Setup(x => x.Get(_lockKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResult<KVPair>
                {
                    Response = new KVPair(_lockKey)
                    {
                        Session = "a-live-session",
                        Value = Encoding.UTF8.GetBytes("some-instance")
                    }
                });

            using var election = Create();

            Assert.Equal("some-instance", await election.GetCurrentLeaderAsync());
        }

        [Fact]
        public async Task GetCurrentLeaderAsync_ReturnsEmpty_WhenNoSessionHoldsTheKey()
        {
            // Lease sessions use Release behaviour, so a failed leader leaves its id in
            // the value with no session attached. Reporting that would name an instance
            // that is no longer leading.
            _kvEndpointMock
                .Setup(x => x.Get(_lockKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResult<KVPair>
                {
                    Response = new KVPair(_lockKey)
                    {
                        Session = null,
                        Value = Encoding.UTF8.GetBytes("the-previous-leader")
                    }
                });

            using var election = Create();

            Assert.Equal(string.Empty, await election.GetCurrentLeaderAsync());
        }

        [Fact]
        public async Task GetCurrentLeaderAsync_ReturnsEmpty_WhenTheKeyIsAbsent()
        {
            _kvEndpointMock
                .Setup(x => x.Get(_lockKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResult<KVPair> { Response = null! });

            using var election = Create();

            Assert.Equal(string.Empty, await election.GetCurrentLeaderAsync());
        }

        [Fact]
        public async Task GetCurrentLeaderAsync_ToleratesANullValue()
        {
            // Consul represents an empty value as a null byte array, which used to throw.
            _kvEndpointMock
                .Setup(x => x.Get(_lockKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResult<KVPair>
                {
                    Response = new KVPair(_lockKey) { Session = "a-live-session", Value = null }
                });

            using var election = Create();

            Assert.Equal(string.Empty, await election.GetCurrentLeaderAsync());
        }

        [Fact]
        public async Task GetCurrentLeaderAsync_WrapsFailures()
        {
            _kvEndpointMock
                .Setup(x => x.Get(_lockKey, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("KV get failed"));

            using var election = Create();

            await Assert.ThrowsAsync<ConsulException>(() => election.GetCurrentLeaderAsync());
        }

        [Fact]
        public void Dispose_DoesNotDisposeAnInjectedClient()
        {
            // The client is normally a shared singleton. Disposing it here would break
            // every other consumer of it.
            using (var election = Create())
            {
            }

            _consulClientMock.Verify(x => x.Dispose(), Times.Never);
        }

        [Fact]
        public async Task UsingAfterDisposal_Throws()
        {
            var election = Create();
            await election.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => election.GetCurrentLeaderAsync());
        }

        [Fact]
        public async Task DisposingTwice_IsSafe()
        {
            var election = Create();

            await election.DisposeAsync();
            await election.DisposeAsync();
            election.Dispose();
        }
    }
}
