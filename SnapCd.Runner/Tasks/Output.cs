using Microsoft.AspNetCore.SignalR.Client;
using SnapCd.Common;
using SnapCd.Common.RunnerRequests;

namespace SnapCd.Runner.Tasks;

public partial class Tasks
{
    public async Task Output(OutputRequestBase request, HubConnection connection)
    {
        var killCts = new CancellationTokenSource();
        _processRegistry.Register(request.JobId, killCts, CancellationType.ImmediateKill);

        var gracefulCts = new CancellationTokenSource();
        _processRegistry.Register(request.JobId, gracefulCts, CancellationType.ImmediateGraceful);

        // Start periodic task reporting
        var reportingCts = CancellationTokenSource.CreateLinkedTokenSource(killCts.Token, gracefulCts.Token);
        var reportingTask = StartPeriodicTaskReporting(
            request.JobId,
            nameof(Output),
            connection,
            TimeSpan.FromSeconds(request.ReportActiveJobFrequencySeconds),
            reportingCts.Token);

        var logger = _loggerFactory.CreateLogger<Tasks>();
        var taskContext = new TaskContext(
            request.JobId,
            nameof(Output),
            logger,
            request.Metadata
        );

        var runnerHubClient = new RunnerHubClient(connection);

        try
        {
            taskContext.LogInformation("Now outputting");

            // Validate hooks against pre-approved hooks
            _hookPreapprovalService.ValidateHooks(
                (request.OutputBeforeHook, nameof(request.OutputBeforeHook)),
                (request.OutputAfterHook, nameof(request.OutputAfterHook))
            );

            var engine = _engineFactory.Create(
                taskContext,
                request.Engine,
                request.Metadata
            );

            var moduleOutputJson = await engine.Output(request.OutputBeforeHook, request.OutputAfterHook, killCts.Token, gracefulCts.Token);

            var moduleOutputSet = await engine.ParseJsonToModuleOutputSet(moduleOutputJson);

            await InvokeWithRetryAsync(
                () => runnerHubClient.InvokeOutputCompleted(request.JobId, moduleOutputSet),
                nameof(runnerHubClient.InvokeOutputCompleted),
                request.JobId,
                connection);
        }
        catch (OperationCanceledException)
        {
            taskContext.LogWarning("Output process was cancelled.");
            await InvokeWithRetryAsync(
                () => runnerHubClient.InvokeOutputCancelled(request.JobId),
                nameof(runnerHubClient.InvokeOutputCancelled),
                request.JobId,
                connection);
        }
        catch (Exception ex)
        {
            taskContext.LogError($"Unhandled exception occurred. {ex.Message}");
            await InvokeWithRetryAsync(
                () => runnerHubClient.InvokeOutputFaulted(request.JobId, ex.Message, ex.StackTrace),
                nameof(runnerHubClient.InvokeOutputFaulted),
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