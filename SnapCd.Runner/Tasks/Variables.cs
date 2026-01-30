using Microsoft.AspNetCore.SignalR.Client;
using SnapCd.Common;
using SnapCd.Common.RunnerRequests;

namespace SnapCd.Runner.Tasks;

public partial class Tasks
{
    public async Task Variables(VariablesRequestBase request, HubConnection connection)
    {
        var killCts = new CancellationTokenSource();
        _processRegistry.Register(request.JobId, killCts, CancellationType.ImmediateKill);

        var gracefulCts = new CancellationTokenSource();
        _processRegistry.Register(request.JobId, gracefulCts, CancellationType.ImmediateGraceful);

        // Start periodic task reporting
        var reportingCts = CancellationTokenSource.CreateLinkedTokenSource(killCts.Token, gracefulCts.Token);
        var reportingTask = StartPeriodicTaskReporting(
            request.JobId,
            nameof(Variables),
            connection,
            TimeSpan.FromSeconds(request.ReportActiveJobFrequencySeconds),
            reportingCts.Token);

        var logger = _loggerFactory.CreateLogger<Tasks>();
        var taskContext = new TaskContext(
            request.JobId,
            nameof(Variables),
            logger,
            request.Metadata
        );

        var runnerHubClient = new RunnerHubClient(connection);

        try
        {
            taskContext.LogInformation("Now discovering variables");

            var engine = _engineFactory.Create(
                taskContext,
                request.Engine,
                request.Metadata
            );

            var extraFileNames = request.ExtraFileNames != null
                ? new HashSet<string>(request.ExtraFileNames)
                : null;

            var variableSetDto = await _discoveryService.CreateVariableSet(
                engine.GetInitDir(),
                request.Metadata.ModuleId,
                extraFileNames);

            await InvokeWithRetryAsync(
                () => runnerHubClient.InvokeVariablesCompleted(request.JobId, variableSetDto),
                nameof(runnerHubClient.InvokeVariablesCompleted),
                request.JobId,
                connection);

            taskContext.LogInformation("Completed Variables");
        }
        catch (OperationCanceledException)
        {
            taskContext.LogWarning("Variables process was cancelled.");
            await InvokeWithRetryAsync(
                () => runnerHubClient.InvokeVariablesCancelled(request.JobId),
                nameof(runnerHubClient.InvokeVariablesCancelled),
                request.JobId,
                connection);
        }
        catch (Exception ex)
        {
            taskContext.LogError($"Unhandled exception occurred. {ex.Message}");
            logger.LogError(ex, "Error handling Variables for job {JobId}", request.JobId);
            await InvokeWithRetryAsync(
                () => runnerHubClient.InvokeVariablesFaulted(
                    request.JobId,
                    ex.Message,
                    ex.StackTrace
                ),
                nameof(runnerHubClient.InvokeVariablesFaulted),
                request.JobId,
                connection);
        }
        finally
        {
            // Stop periodic reporting
            reportingCts?.Cancel();
            if (reportingTask != null)
            {
                try { await reportingTask; }
                catch { /* Already logged */ }
            }

            _processRegistry.Remove(request.JobId, CancellationType.ImmediateKill);
            _processRegistry.Remove(request.JobId, CancellationType.ImmediateGraceful);
        }
    }
}