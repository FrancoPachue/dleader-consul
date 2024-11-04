using DLeader.Consul.Abstractions;
using Consul;
using System.Text.Json;

namespace DLeader.Consul.Example
{
    public class ScheduledTasksService : BackgroundService
    {
        private readonly ILeaderElection _leaderElection;
        private readonly IMessageBroker _messageBroker;
        private readonly ILogger<ScheduledTasksService> _logger;
        private readonly IConsulClient _consulClient;
        private readonly Dictionary<string, TaskDefinition> _taskDefinitions = new();
        private readonly string _nodeId;
        private bool _isInitialized = false;

        public ScheduledTasksService(
            ILeaderElection leaderElection,
            IMessageBroker messageBroker,
            ILogger<ScheduledTasksService> logger,
            IConsulClient consulClient)
        {
            _leaderElection = leaderElection;
            _messageBroker = messageBroker;
            _logger = logger;
            _consulClient = consulClient;
            _nodeId = Guid.NewGuid().ToString();

            InitializeTaskDefinitions();
            _messageBroker.SubscribeAsync("task-result", HandleTaskResult);
        }

        private void InitializeTaskDefinitions()
        {
            _taskDefinitions.Add("daily-cleanup", new TaskDefinition
            {
                Name = "daily-cleanup",
                Interval = TimeSpan.FromHours(24),
                Description = "Daily system cleanup task",
                TaskAction = PerformCleanup,
                Priority = TaskPriority.Low
            });

            _taskDefinitions.Add("hourly-sync", new TaskDefinition
            {
                Name = "hourly-sync",
                Interval = TimeSpan.FromHours(1),
                Description = "Hourly data synchronization",
                TaskAction = PerformSync,
                Priority = TaskPriority.Medium
            });

            _taskDefinitions.Add("weekly-report", new TaskDefinition
            {
                Name = "weekly-report",
                Interval = TimeSpan.FromDays(7),
                Description = "Weekly status report generation",
                TaskAction = GenerateWeeklyReport,
                Priority = TaskPriority.Low
            });

            _taskDefinitions.Add("monitoring-check", new TaskDefinition
            {
                Name = "monitoring-check",
                Interval = TimeSpan.FromMinutes(5),
                Description = "System health monitoring check",
                TaskAction = PerformMonitoringCheck,
                Priority = TaskPriority.High
            });
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await _leaderElection.StartLeaderElectionAsync(stoppingToken);
                _logger.LogInformation("Scheduled tasks service started on node {NodeId}", _nodeId);

                while (!stoppingToken.IsCancellationRequested)
                {
                    if (await _leaderElection.IsLeaderAsync())
                    {
                        await InitializeIfNeeded();
                        await CheckAndExecuteScheduledTasks(stoppingToken);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in scheduled tasks service");
                throw;
            }
        }

        private async Task InitializeIfNeeded()
        {
            if (!_isInitialized)
            {
                _logger.LogInformation("Initializing scheduled tasks service");
                await EnsureTaskStatesExist();
                _isInitialized = true;
            }
        }

        private async Task EnsureTaskStatesExist()
        {
            foreach (var task in _taskDefinitions.Values)
            {
                var lastExecution = await GetLastExecutionFromStore(task.Name);
                if (lastExecution == null)
                {
                    await SaveTaskState(task.Name, new TaskState
                    {
                        LastExecution = DateTime.UtcNow,
                        LastStatus = TaskStatus.NotRun,
                        LastRunDuration = TimeSpan.Zero,
                        SuccessfulRuns = 0,
                        FailedRuns = 0
                    });
                }
            }
        }

        private async Task<TaskState?> GetTaskState(string taskName)
        {
            try
            {
                var key = $"tasks/{taskName}/state";
                var response = await _consulClient.KV.Get(key);

                if (response.Response != null)
                {
                    var value = System.Text.Encoding.UTF8.GetString(response.Response.Value);
                    return JsonSerializer.Deserialize<TaskState>(value);
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving state for task {TaskName}", taskName);
                return null;
            }
        }

        private async Task<DateTime?> GetLastExecutionFromStore(string taskName)
        {
            var state = await GetTaskState(taskName);
            return state?.LastExecution;
        }

        private async Task SaveTaskState(string taskName, TaskState state)
        {
            try
            {
                var key = $"tasks/{taskName}/state";
                var value = JsonSerializer.Serialize(state);
                var pair = new KVPair(key)
                {
                    Value = System.Text.Encoding.UTF8.GetBytes(value)
                };

                await _consulClient.KV.Put(pair);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving state for task {TaskName}", taskName);
            }
        }

        private async Task CheckAndExecuteScheduledTasks(CancellationToken stoppingToken)
        {
            foreach (var task in _taskDefinitions.Values.OrderByDescending(t => t.Priority))
            {
                if (stoppingToken.IsCancellationRequested) break;

                var state = await GetTaskState(task.Name);
                if (state == null) continue;

                if (DateTime.UtcNow - state.LastExecution >= task.Interval)
                {
                    await ExecuteTask(task, stoppingToken);
                }
            }
        }

        private async Task ExecuteTask(TaskDefinition task, CancellationToken stoppingToken)
        {
            _logger.LogInformation("Executing scheduled task: {TaskName} - {Description}",
                task.Name, task.Description);

            var state = await GetTaskState(task.Name) ?? new TaskState();
            var startTime = DateTime.UtcNow;

            try
            {
                await _messageBroker.BroadcastAsync("task-start",
                    JsonSerializer.Serialize(new
                    {
                        TaskName = task.Name,
                        StartTime = startTime,
                        Priority = task.Priority
                    }));

                await task.TaskAction(stoppingToken);

                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                state.LastExecution = endTime;
                state.LastStatus = TaskStatus.Success;
                state.LastRunDuration = duration;
                state.SuccessfulRuns++;

                await SaveTaskState(task.Name, state);

                await _messageBroker.BroadcastAsync("task-completion",
                    JsonSerializer.Serialize(new
                    {
                        TaskName = task.Name,
                        StartTime = startTime,
                        EndTime = endTime,
                        Duration = duration.TotalSeconds,
                        Status = TaskStatus.Success,
                        Priority = task.Priority
                    }));

                _logger.LogInformation(
                    "Task {TaskName} completed successfully. Duration: {Duration:F2} seconds",
                    task.Name, duration.TotalSeconds);
            }
            catch (Exception ex)
            {
                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                state.LastExecution = endTime;
                state.LastStatus = TaskStatus.Failed;
                state.LastRunDuration = duration;
                state.FailedRuns++;
                state.LastError = ex.Message;

                await SaveTaskState(task.Name, state);

                _logger.LogError(ex, "Error executing task: {TaskName}", task.Name);
                await _messageBroker.BroadcastAsync("task-error",
                    JsonSerializer.Serialize(new
                    {
                        TaskName = task.Name,
                        Error = ex.Message,
                        Duration = duration.TotalSeconds,
                        Timestamp = endTime
                    }));
            }
        }

        private async Task HandleTaskResult(string message)
        {
            try
            {
                var result = JsonSerializer.Deserialize<TaskResult>(message);
                _logger.LogInformation(
                    "Received task result: {TaskName} - Status: {Status}",
                    result.TaskName, result.Status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling task result");
            }
        }

        #region Task Implementations

        private async Task PerformCleanup(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting daily cleanup process");

            // Simular limpieza de archivos temporales
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            _logger.LogInformation("Cleaned temporary files");

            // Simular limpieza de caché
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            _logger.LogInformation("Cleaned cache");

            // Simular limpieza de logs antiguos
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            _logger.LogInformation("Cleaned old logs");
        }

        private async Task PerformSync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting hourly sync process");

            // Simular sincronización de datos
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            _logger.LogInformation("Data synchronized with remote systems");
        }

        private async Task GenerateWeeklyReport(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting weekly report generation");

            // Simular recopilación de datos
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            _logger.LogInformation("Data collected for report");

            // Simular generación de reporte
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            _logger.LogInformation("Report generated");

            // Simular envío de reporte
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            _logger.LogInformation("Report sent to stakeholders");
        }

        private async Task PerformMonitoringCheck(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting monitoring check");

            // Simular verificación de recursos
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            var metrics = new
            {
                CpuUsage = Random.Shared.Next(0, 100),
                MemoryUsage = Random.Shared.Next(0, 100),
                DiskSpace = Random.Shared.Next(20, 95)
            };

            await _messageBroker.BroadcastAsync("monitoring-metrics",
                JsonSerializer.Serialize(metrics));

            _logger.LogInformation(
                "Monitoring check completed. CPU: {CPU}%, Memory: {Memory}%, Disk: {Disk}%",
                metrics.CpuUsage, metrics.MemoryUsage, metrics.DiskSpace);
        }

        #endregion

        #region Supporting Classes

        private class TaskDefinition
        {
            public string Name { get; set; }
            public TimeSpan Interval { get; set; }
            public string Description { get; set; }
            public Func<CancellationToken, Task> TaskAction { get; set; }
            public TaskPriority Priority { get; set; }
        }

        private class TaskState
        {
            public DateTime LastExecution { get; set; }
            public TaskStatus LastStatus { get; set; }
            public TimeSpan LastRunDuration { get; set; }
            public int SuccessfulRuns { get; set; }
            public int FailedRuns { get; set; }
            public string? LastError { get; set; }
        }

        private class TaskResult
        {
            public string TaskName { get; set; }
            public TaskStatus Status { get; set; }
            public DateTime Timestamp { get; set; }
        }

        private enum TaskStatus
        {
            NotRun,
            Running,
            Success,
            Failed
        }

        private enum TaskPriority
        {
            Low,
            Medium,
            High
        }

        #endregion
    }
}