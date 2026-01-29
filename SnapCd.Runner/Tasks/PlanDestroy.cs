using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using SnapCd.Common;
using SnapCd.Common.RunnerRequests;
using SnapCd.Common.RunnerRequests.HelperClasses;

namespace SnapCd.Runner.Tasks;

public partial class Tasks
{
    public async Task PlanDestroy(PlanDestroyRequestBase request, HubConnection connection)
    {
        var killCts = new CancellationTokenSource();
        _processRegistry.Register(request.JobId, killCts, CancellationType.ImmediateKill);

        var gracefulCts = new CancellationTokenSource();
        _processRegistry.Register(request.JobId, gracefulCts, CancellationType.ImmediateGraceful);

        // Start periodic task reporting
        var reportingCts = CancellationTokenSource.CreateLinkedTokenSource(killCts.Token, gracefulCts.Token);
        var reportingTask = StartPeriodicTaskReporting(
            request.JobId,
            nameof(PlanDestroy),
            connection,
            TimeSpan.FromSeconds(request.ReportActiveJobFrequencySeconds),
            reportingCts.Token);

        var logger = _loggerFactory.CreateLogger<Tasks>();
        var taskContext = new TaskContext(
            request.JobId,
            nameof(PlanDestroy),
            logger,
            request.Metadata
        );

        var runnerHubClient = new RunnerHubClient(connection);

        try
        {
            taskContext.LogInformation("Now planning destroy");

            // Validate hooks against pre-approved hooks
            _hookPreapprovalService.ValidateHooks(
                (request.PlanDestroyBeforeHook, nameof(request.PlanDestroyBeforeHook)),
                (request.PlanDestroyAfterHook, nameof(request.PlanDestroyAfterHook))
            );

            var engine = _engineFactory.Create(
                taskContext,
                request.Engine,
                request.Metadata
            );

            await engine.PlanDestroy(request.ResolvedParameters, request.PlanDestroyBeforeHook, request.PlanDestroyAfterHook, killCts.Token, gracefulCts.Token);

            var planDestroy = engine.ParseDestroyPlan();

            // Extract resource counts and changes
            var unchangedResourcesCount = planDestroy.GetResourceCount(Tfplan.Action.Noop);
            var createResourcesCount = planDestroy.GetResourceCount(Tfplan.Action.Create);
            var modifyResourcesCount = planDestroy.GetResourceCount(Tfplan.Action.Update);
            var destroyResourcesCount = planDestroy.GetResourceCount(Tfplan.Action.Delete);
            var recreateResourcesCount = planDestroy.GetResourceCount(Tfplan.Action.DeleteThenCreate);
            var totalChangedResourcesCount = createResourcesCount + modifyResourcesCount + destroyResourcesCount + recreateResourcesCount;
            var totalResourcesCountAfter = unchangedResourcesCount + modifyResourcesCount + recreateResourcesCount + createResourcesCount;
            var totalResourcesCountBefore = unchangedResourcesCount + modifyResourcesCount + recreateResourcesCount + destroyResourcesCount;

            taskContext.LogInformation(
                $"PlanDestroy summary:\n- Unchanged: {unchangedResourcesCount}\n- Create:    {createResourcesCount}\n- Modify:    {modifyResourcesCount}\n- Destroy:   {destroyResourcesCount}\n- Recreate:  {recreateResourcesCount}\n- Count Before Apply:  {totalResourcesCountBefore}\n- Count After Apply:   {totalResourcesCountAfter}");

            // Extract output counts and changes
            var unchangedOutputsCount = planDestroy.GetOutputCount(Tfplan.Action.Noop);
            var createOutputsCount = planDestroy.GetOutputCount(Tfplan.Action.Create);
            var modifyOutputsCount = planDestroy.GetOutputCount(Tfplan.Action.Update);
            var destroyOutputsCount = planDestroy.GetOutputCount(Tfplan.Action.Delete);
            var recreateOutputsCount = planDestroy.GetOutputCount(Tfplan.Action.DeleteThenCreate);
            var totalChangedOutputsCount = createOutputsCount + modifyOutputsCount + destroyOutputsCount + recreateOutputsCount;

            taskContext.LogInformation(
                $"PlanDestroy summary:\n- Unchanged Outputs: {unchangedOutputsCount}\n- Create Outputs:    {createOutputsCount}\n- Modify Outputs:    {modifyOutputsCount}\n- Destroyed Outputs: {destroyOutputsCount}\n- Recreate Outputs:  {recreateOutputsCount}");

            // Build response data
            var planData = new PlanCompletedData
            {
                TotalCountAfter = totalResourcesCountAfter,
                TotalCountBefore = totalResourcesCountBefore,
                TotalChangedCount = totalChangedResourcesCount,
                TotalUnchangedCount = unchangedResourcesCount,
                CreateCount = createResourcesCount,
                ModifyCount = modifyResourcesCount,
                DestroyCount = destroyResourcesCount,
                RecreateCount = recreateResourcesCount,
                OutputsTotalCount = totalChangedOutputsCount + unchangedOutputsCount,
                OutputsTotalChangedCount = totalChangedOutputsCount,
                OutputsTotalUnchangedCount = unchangedOutputsCount,
                OutputsCreateCount = createOutputsCount,
                OutputsModifyCount = modifyOutputsCount,
                OutputsDestroyCount = destroyOutputsCount,
                OutputsRecreateCount = recreateOutputsCount,
                OutputsUnchangedList = planDestroy.GetOutputChange(Tfplan.Action.Noop).Select(o => o.Name).ToList(),
                OutputsCreateList = planDestroy.GetOutputChange(Tfplan.Action.Create).Select(o => o.Name).ToList(),
                OutputsModifyList = planDestroy.GetOutputChange(Tfplan.Action.Update).Select(o => o.Name).ToList(),
                OutputsDestroyList = planDestroy.GetOutputChange(Tfplan.Action.Delete).Select(o => o.Name).ToList(),
                OutputsRecreateList = planDestroy.GetOutputChange(Tfplan.Action.DeleteThenCreate).Select(o => o.Name).ToList()
            };

            await InvokeWithRetryAsync(
                () => runnerHubClient.InvokePlanDestroyCompleted(request.JobId, planData),
                nameof(runnerHubClient.InvokePlanDestroyCompleted),
                request.JobId,
                connection);

            taskContext.LogInformation("Completed PlanDestroy");
        }
        catch (OperationCanceledException)
        {
            taskContext.LogWarning("Destroy plan process was cancelled.");
            await InvokeWithRetryAsync(
                () => runnerHubClient.InvokePlanDestroyCancelled(request.JobId),
                nameof(runnerHubClient.InvokePlanDestroyCancelled),
                request.JobId,
                connection);
        }
        catch (Exception ex)
        {
            taskContext.LogError($"Unhandled exception occurred. {ex.Message}");
            logger.LogError(ex, "Error handling PlanDestroy for job {JobId}", request.JobId);
            await InvokeWithRetryAsync(
                () => runnerHubClient.InvokePlanDestroyFaulted(
                    request.JobId,
                    ex.Message,
                    ex.StackTrace
                ),
                nameof(runnerHubClient.InvokePlanDestroyFaulted),
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