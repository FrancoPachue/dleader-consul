using Xunit;
using Moq;
using Consul;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DLeader.Consul.Configuration;
using System.Text;
using DLeader.Consul.Implementations;
using DLeader.Consul.Exceptions;
using System.Net;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace DLeader.Consul.Tests.Implementations
{
    public class ConsulLeaderElectionTests
    {
        private readonly Mock<ILogger<ConsulLeaderElection>> _loggerMock;
        private readonly Mock<IConsulClient> _consulClientMock;
        private readonly Mock<IKVEndpoint> _kvEndpointMock;
        private readonly Mock<IAgentEndpoint> _agentEndpointMock;
        private readonly Mock<ISessionEndpoint> _sessionEndpointMock;
        private readonly ConsulOptions _consulOptions;
        private readonly ServiceRegistrationOptions _serviceOptions;
        private readonly string _testSessionId = "test-session-id";
        private readonly string _lockKey;

        public ConsulLeaderElectionTests()
        {
            _loggerMock = new Mock<ILogger<ConsulLeaderElection>>();
            _consulClientMock = new Mock<IConsulClient>();
            _kvEndpointMock = new Mock<IKVEndpoint>();
            _agentEndpointMock = new Mock<IAgentEndpoint>();
            _sessionEndpointMock = new Mock<ISessionEndpoint>();

            _consulOptions = new ConsulOptions
            {
                ServiceName = "test-service",
                Address = "http://localhost:8500",
                SessionTTL = 10,
                LeaderCheckInterval = 5,
                RenewInterval = 5
            };

            _serviceOptions = new ServiceRegistrationOptions
            {
                ServicePort = 5000
            };

            _lockKey = $"service/{_consulOptions.ServiceName}/leader";

            _consulClientMock.Setup(x => x.KV).Returns(_kvEndpointMock.Object);
            _consulClientMock.Setup(x => x.Agent).Returns(_agentEndpointMock.Object);
            _consulClientMock.Setup(x => x.Session).Returns(_sessionEndpointMock.Object);
        }

        private ConsulLeaderElection CreateLeaderElection()
        {
            return new ConsulLeaderElection(
                _loggerMock.Object,
                Options.Create(_consulOptions),
                Options.Create(_serviceOptions),
                _consulClientMock.Object
            );
        }

        private void ConfigureSuccessfulStartup()
        {
            var leaderElection = CreateLeaderElection();
            var instanceId = leaderElection.InstanceId;

            // Configurar secuencia de respuestas para Services
            _agentEndpointMock
                .SetupSequence(x => x.Services(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>> 
                { 
                    Response = new Dictionary<string, AgentService>() 
                })
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>>
                {
                    Response = new Dictionary<string, AgentService>
                    {
                        {
                            instanceId,
                            new AgentService
                            {
                                ID = instanceId,
                                Service = _consulOptions.ServiceName,
                                Port = _serviceOptions.ServicePort,
                                Address = "localhost"
                            }
                        }
                    }
                });

            // Resto de la configuración
            _agentEndpointMock
                .Setup(x => x.ServiceRegister(It.IsAny<AgentServiceRegistration>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WriteResult<bool>());

            _sessionEndpointMock
                .Setup(x => x.Create(It.IsAny<SessionEntry>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WriteResult<string> { Response = _testSessionId });

            _kvEndpointMock
                .Setup(x => x.Acquire(It.IsAny<KVPair>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WriteResult<bool> { Response = true });

            _kvEndpointMock
                .Setup(x => x.Get(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResult<KVPair>
                {
                    Response = new KVPair(_lockKey)
                    {
                        Value = Encoding.UTF8.GetBytes(instanceId)
                    }
                });
        }

        [Fact]
        public async Task StartLeaderElectionAsync_Success()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();

            _agentEndpointMock
                .SetupSequence(x => x.Services(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>> 
                { 
                    Response = new Dictionary<string, AgentService>() 
                })
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>> 
                { 
                    Response = new Dictionary<string, AgentService>() 
                })
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>>
                {
                    Response = new Dictionary<string, AgentService>
                    {
                        {
                            leaderElection.InstanceId,
                            new AgentService
                            {
                                ID = leaderElection.InstanceId,
                                Service = _consulOptions.ServiceName,
                                Port = _serviceOptions.ServicePort,
                                Address = "localhost"
                            }
                        }
                    }
                });

            // Configurar ServiceRegister para que retorne éxito
            _agentEndpointMock
                .Setup(x => x.ServiceRegister(
                    It.Is<AgentServiceRegistration>(r => 
                        r.ID == leaderElection.InstanceId && 
                        r.Name == _consulOptions.ServiceName &&
                        r.Port == _serviceOptions.ServicePort),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WriteResult<bool>())
                .Verifiable();

            // Configurar Session
            _sessionEndpointMock
                .Setup(x => x.Create(It.IsAny<SessionEntry>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WriteResult<string> { Response = _testSessionId });

            // Configurar KV
            _kvEndpointMock
                .Setup(x => x.Acquire(It.IsAny<KVPair>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WriteResult<bool> { Response = true });

            // Act
            await leaderElection.StartLeaderElectionAsync(CancellationToken.None);

            // Assert
            _agentEndpointMock.Verify(
                x => x.ServiceRegister(
                    It.Is<AgentServiceRegistration>(r => 
                        r.ID == leaderElection.InstanceId && 
                        r.Name == _consulOptions.ServiceName &&
                        r.Port == _serviceOptions.ServicePort),
                    It.IsAny<CancellationToken>()),
                Times.Once);

            // Verificar los logs esperados
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Registering service with ID")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task StartLeaderElectionAsync_CleansUpStaleServices()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();
            var staleServices = new Dictionary<string, AgentService>
            {
                {
                    "stale-service-1",
                    new AgentService
                    {
                        ID = "stale-service-1",
                        Service = _consulOptions.ServiceName,
                        Port = 5001,
                        Address = "localhost"
                    }
                },
                {
                    "stale-service-2",
                    new AgentService
                    {
                        ID = "stale-service-2",
                        Service = _consulOptions.ServiceName,
                        Port = 5002,
                        Address = "localhost"
                    }
                }
            };

            // Primera llamada: servicios antiguos, segunda llamada: servicio registrado
            _agentEndpointMock
                .SetupSequence(x => x.Services(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>>
                {
                    Response = staleServices
                })
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>>
                {
                    Response = new Dictionary<string, AgentService>
                    {
                        {
                            leaderElection.InstanceId,
                            new AgentService
                            {
                                ID = leaderElection.InstanceId,
                                Service = _consulOptions.ServiceName,
                                Port = _serviceOptions.ServicePort,
                                Address = "localhost"
                            }
                        }
                    }
                });

            // Resto de la configuración básica
            ConfigureBasicMocks(leaderElection);

            // Act
            await leaderElection.StartLeaderElectionAsync(CancellationToken.None);

            // Assert
            _agentEndpointMock.Verify(
                x => x.ServiceDeregister("stale-service-1", It.IsAny<CancellationToken>()),
                Times.Once);
            _agentEndpointMock.Verify(
                x => x.ServiceDeregister("stale-service-2", It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task StartLeaderElectionAsync_HandlesServiceVerificationFailure()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();

            _agentEndpointMock
                .SetupSequence(x => x.Services(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Service verification failed"))
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>>
                {
                    Response = new Dictionary<string, AgentService>
                    {
                        {
                            leaderElection.InstanceId,
                            new AgentService
                            {
                                ID = leaderElection.InstanceId,
                                Service = _consulOptions.ServiceName,
                                Port = _serviceOptions.ServicePort,
                                Address = "localhost"
                            }
                        }
                    }
                });

            ConfigureBasicMocks(leaderElection);

            // Act
            await leaderElection.StartLeaderElectionAsync(CancellationToken.None);

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => true),
                    It.IsAny<Exception>(),
                    It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)),
                Times.AtLeastOnce);
        }

        [Fact]
        public async Task IsLeaderAsync_ReturnsTrue_WhenInstanceIsLeader()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();
            ConfigureSuccessfulStartup();

            // Act
            var isLeader = await leaderElection.IsLeaderAsync();

            // Assert
            Assert.True(isLeader);
        }

        [Fact]
        public async Task GetCurrentLeaderAsync_ReturnsInstanceId_WhenInstanceIsLeader()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();
            ConfigureSuccessfulStartup();

            // Act
            var currentLeader = await leaderElection.GetCurrentLeaderAsync();

            // Assert
            Assert.Equal(leaderElection.InstanceId, currentLeader);
        }

        [Fact]
        public void Dispose_CleansUpResources()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();
            ConfigureSuccessfulStartup();

            // Act
            leaderElection.Dispose();

            // Assert
            _agentEndpointMock.Verify(
                x => x.ServiceDeregister(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task DisposeAsync_CleansUpResourcesAsync()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();
            ConfigureSuccessfulStartup();

            // Act
            await leaderElection.DisposeAsync();

            // Assert
            _agentEndpointMock.Verify(
                x => x.ServiceDeregister(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task RaiseLeadershipAcquiredEvent_InvokesHandler()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();
            var handlerCalled = false;
            leaderElection.OnLeadershipAcquired += async () => { handlerCalled = true; await Task.CompletedTask; };

            // Act
            await leaderElection.RaiseLeadershipAcquiredEvent();

            // Assert
            Assert.True(handlerCalled);
        }

        [Fact]
        public async Task RaiseLeadershipLostEvent_InvokesHandler()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();
            var handlerCalled = false;
            leaderElection.OnLeadershipLost += async () => { handlerCalled = true; await Task.CompletedTask; };

            // Act
            await leaderElection.RaiseLeadershipLostEvent();

            // Assert
            Assert.True(handlerCalled);
        }

        [Fact]
        public async Task StartLeaderElectionAsync_HandlesSessionCreationFailure()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();
            ConfigureSuccessfulServiceRegistration(leaderElection);

            _sessionEndpointMock
                .Setup(x => x.Create(It.IsAny<SessionEntry>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Session creation failed"));

            // Act & Assert
            await Assert.ThrowsAsync<LeadershipException>(() =>
                leaderElection.StartLeaderElectionAsync(CancellationToken.None));
        }

        [Fact]
        public async Task StartLeaderElectionAsync_HandlesKVAcquireFailure()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();
            ConfigureSuccessfulServiceRegistration(leaderElection);

            _kvEndpointMock
                .Setup(x => x.Acquire(It.IsAny<KVPair>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("KV acquire failed"));

            // Act
            await leaderElection.StartLeaderElectionAsync(CancellationToken.None);

            // Assert
            await Task.Delay(100);

            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Error in leader election loop")),
                    It.Is<Exception>(ex => ex.Message == "KV acquire failed"),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.AtLeastOnce);
        }

        [Fact]
        public async Task GetCurrentLeaderAsync_HandlesNullResponse()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();

            _kvEndpointMock
                .Setup(x => x.Get(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResult<KVPair> { Response = null });

            // Act
            var result = await leaderElection.GetCurrentLeaderAsync();

            // Assert
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public async Task GetCurrentLeaderAsync_HandlesException()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();

            _kvEndpointMock
                .Setup(x => x.Get(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("KV get failed"));

            // Act & Assert
            await Assert.ThrowsAsync<ConsulException>(() => 
                leaderElection.GetCurrentLeaderAsync());
        }

        [Fact]
        public void Dispose_HandlesExceptions()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();

            _agentEndpointMock
                .Setup(x => x.ServiceDeregister(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Deregister failed"));

            // Act & Assert (no debería lanzar excepción)
            leaderElection.Dispose();

            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Error during async disposal")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task RunLeaderElectionAsync_HandlesExceptions()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();
            ConfigureSuccessfulServiceRegistration(leaderElection);

            _kvEndpointMock
                .SetupSequence(x => x.Acquire(It.IsAny<KVPair>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("First attempt failed"))
                .ReturnsAsync(new WriteResult<bool> { Response = true });

            // Act
            await leaderElection.StartLeaderElectionAsync(CancellationToken.None);

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Error in leader election loop")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);
        }

        // Método auxiliar para configurar los mocks básicos
        private void ConfigureBasicMocks(ConsulLeaderElection leaderElection)
        {
            _agentEndpointMock
                .Setup(x => x.ServiceRegister(It.IsAny<AgentServiceRegistration>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WriteResult<bool>());

            _sessionEndpointMock
                .Setup(x => x.Create(It.IsAny<SessionEntry>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WriteResult<string> { Response = _testSessionId });

            _kvEndpointMock
                .Setup(x => x.Acquire(It.IsAny<KVPair>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WriteResult<bool> { Response = true });

            _kvEndpointMock
                .Setup(x => x.Get(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResult<KVPair>
                {
                    Response = new KVPair(_lockKey)
                    {
                        Value = Encoding.UTF8.GetBytes(leaderElection.InstanceId)
                    }
                });
        }

        // Método auxiliar para configurar un registro de servicio exitoso
        private void ConfigureSuccessfulServiceRegistration(ConsulLeaderElection leaderElection)
        {
            _agentEndpointMock
                .SetupSequence(x => x.Services(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>> 
                { 
                    Response = new Dictionary<string, AgentService>() 
                })
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>> 
                { 
                    Response = new Dictionary<string, AgentService>() 
                })
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>>
                {
                    Response = new Dictionary<string, AgentService>
                    {
                        {
                            leaderElection.InstanceId,
                            new AgentService
                            {
                                ID = leaderElection.InstanceId,
                                Service = _consulOptions.ServiceName,
                                Port = _serviceOptions.ServicePort,
                                Address = "localhost"
                            }
                        }
                    }
                });

            _sessionEndpointMock
                .Setup(x => x.Create(It.IsAny<SessionEntry>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WriteResult<string> { Response = _testSessionId });
        }

        [Fact]
        public async Task RenewSession_HandlesException()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();
            var sessionId = "test-session-id";
            var cts = new CancellationTokenSource();

            // Configurar el mock para lanzar una excepción
            _sessionEndpointMock
                .Setup(x => x.Renew(sessionId, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Session renewal failed"));

            // Configurar los mocks necesarios para llegar a RenewSession
            _agentEndpointMock
                .SetupSequence(x => x.Services(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>> 
                { 
                    Response = new Dictionary<string, AgentService>() 
                })
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>>
                {
                    Response = new Dictionary<string, AgentService>
                    {
                        {
                            leaderElection.InstanceId,
                            new AgentService
                            {
                                ID = leaderElection.InstanceId,
                                Service = _consulOptions.ServiceName,
                                Port = _serviceOptions.ServicePort,
                                Address = "localhost"
                            }
                        }
                    }
                });

            _sessionEndpointMock
                .Setup(x => x.Create(It.IsAny<SessionEntry>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WriteResult<string> { Response = sessionId });

            _kvEndpointMock
                .Setup(x => x.Acquire(It.IsAny<KVPair>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WriteResult<bool> { Response = true });

            // Act
            await leaderElection.StartLeaderElectionAsync(cts.Token);
            
            // Esperar un poco para que se ejecute el loop de renovación
            await Task.Delay(TimeSpan.FromSeconds(2));
            
            // Cancelar para detener el loop
            cts.Cancel();

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Error renewing session")),
                    It.Is<Exception>(ex => ex.Message == "Session renewal failed"),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.AtLeastOnce);

            // Verificar que se intentó renovar la sesión
            _sessionEndpointMock.Verify(
                x => x.Renew(sessionId, It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);
        }

        [Fact]
        public async Task RenewSession_SuccessfulRenewal()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();
            var sessionId = "test-session-id";
            var cts = new CancellationTokenSource();

            // Configurar el mock para una renovación exitosa
            _sessionEndpointMock
                .Setup(x => x.Renew(sessionId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WriteResult<SessionEntry>());

            // Configurar los mocks necesarios
            ConfigureSuccessfulServiceRegistration(leaderElection);

            _sessionEndpointMock
                .Setup(x => x.Create(It.IsAny<SessionEntry>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WriteResult<string> { Response = sessionId });

            _kvEndpointMock
                .Setup(x => x.Acquire(It.IsAny<KVPair>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WriteResult<bool> { Response = true });

            // Act
            await leaderElection.StartLeaderElectionAsync(cts.Token);
            
            // Esperar suficiente tiempo para que ocurra al menos una renovación
            await Task.Delay(TimeSpan.FromSeconds(_consulOptions.RenewInterval + 1));
            
            // Cancelar para detener el loop
            cts.Cancel();

            // Assert
            _sessionEndpointMock.Verify(
                x => x.Renew(sessionId, It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);
        }

        [Fact]
        public async Task VerifyServiceRegistration_HandlesNullResponse()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();

            _agentEndpointMock
                .SetupSequence(x => x.Services(It.IsAny<CancellationToken>()))
                // Primera llamada para DeregisterPreviousServiceAsync
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>> 
                { 
                    Response = new Dictionary<string, AgentService>() 
                })
                // Segunda llamada para RegisterServiceAsync
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>> 
                { 
                    Response = new Dictionary<string, AgentService>() 
                })
                // Tercera llamada para VerifyServiceRegistrationAsync - null response
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>> 
                { 
                    Response = null 
                })
                // Cuarta llamada - servicio encontrado
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>>
                {
                    Response = new Dictionary<string, AgentService>
                    {
                        {
                            leaderElection.InstanceId,
                            new AgentService
                            {
                                ID = leaderElection.InstanceId,
                                Service = _consulOptions.ServiceName,
                                Port = _serviceOptions.ServicePort,
                                Address = "localhost"
                            }
                        }
                    }
                });

            ConfigureBasicMocks(leaderElection);

            // Act
            await leaderElection.StartLeaderElectionAsync(CancellationToken.None);

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Consul returned null response when querying services")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task VerifyServiceRegistration_HandlesServiceNotFound()
        {
            // Arrange
            var leaderElection = CreateLeaderElection();

            _agentEndpointMock
                .SetupSequence(x => x.Services(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>> 
                { 
                    Response = new Dictionary<string, AgentService>() 
                })
                // Segunda llamada para RegisterServiceAsync
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>> 
                { 
                    Response = new Dictionary<string, AgentService>() 
                })
                // Tercera, cuarta y quinta llamada - servicio no encontrado
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>>
                {
                    Response = new Dictionary<string, AgentService>()
                })
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>>
                {
                    Response = new Dictionary<string, AgentService>()
                })
                .ReturnsAsync(new QueryResult<Dictionary<string, AgentService>>
                {
                    Response = new Dictionary<string, AgentService>()
                });

            ConfigureBasicMocks(leaderElection);

            // Act
            await leaderElection.StartLeaderElectionAsync(CancellationToken.None);

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Service not found in verification attempt")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Exactly(_consulOptions.VerificationRetries));

            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains($"Service verification did not succeed after {_consulOptions.VerificationRetries} attempts")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);
        }
    }
}