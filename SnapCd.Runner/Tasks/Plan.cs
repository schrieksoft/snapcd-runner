using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using SnapCd.Common;
using SnapCd.Common.RunnerRequests;
using SnapCd.Common.RunnerRequests.HelperClasses;

namespace SnapCd.Runner.Tasks;

public partial class Tasks
{
    public async Task Plan(PlanRequestBase request, HubConnection connection)
    {
        var killCts = new CancellationTokenSource();
        _processRegistry.Register(request.JobId, killCts, CancellationType.ImmediateKill);

        var gracefulCts = new CancellationTokenSource();
        _processRegistry.Register(request.JobId, gracefulCts, CancellationType.ImmediateGraceful);

        // Start periodic task reporting
        var reportingCts = CancellationTokenSource.CreateLinkedTokenSource(killCts.Token, gracefulCts.Token);
        var reportingTask = StartPeriodicTaskReporting(
            request.JobId,
            nameof(Plan),
            connection,
            TimeSpan.FromSeconds(request.ReportActiveJobFrequencySeconds),
            reportingCts.Token);

        var logger = _loggerFactory.CreateLogger<Tasks>();
        var taskContext = new TaskContext(
            request.JobId,
            nameof(Plan),
            logger,
            request.Metadata
        );

        var runnerHubClient = new RunnerHubClient(connection);

        try
        {
            taskContext.LogInformation("Now planning");

            // Validate hooks against pre-approved hooks
            _hookPreapprovalService.ValidateHooks(
                (request.PlanBeforeHook, nameof(request.PlanBeforeHook)),
                (request.PlanAfterHook, nameof(request.PlanAfterHook))
            );

            var engine = _engineFactory.Create(
                taskContext,
                request.Engine,
                request.Metadata
            );

            await engine.Plan(request.ResolvedParameters, request.PlanBeforeHook, request.PlanAfterHook, killCts.Token, gracefulCts.Token);

            var plan = engine.ParseApplyPlan();

            // Extract resource counts and changes
            var unchangedResourcesCount = plan.GetResourceCount(Tfplan.Action.Noop);
            var createResourcesCount = plan.GetResourceCount(Tfplan.Action.Create);
            var modifyResourcesCount = plan.GetResourceCount(Tfplan.Action.Update);
            var destroyResourcesCount = plan.GetResourceCount(Tfplan.Action.Delete);
            var recreateResourcesCount = plan.GetResourceCount(Tfplan.Action.DeleteThenCreate);
            var totalChangedResourcesCount = createResourcesCount + modifyResourcesCount + destroyResourcesCount + recreateResourcesCount;
            var totalResourcesCountAfter = unchangedResourcesCount + modifyResourcesCount + recreateResourcesCount + createResourcesCount;
            var totalResourcesCountBefore = unchangedResourcesCount + modifyResourcesCount + recreateResourcesCount + destroyResourcesCount;

            var unchangedResources = plan.GetResourceChange(Tfplan.Action.Noop);
            var createResources = plan.GetResourceChange(Tfplan.Action.Create);
            var modifyResources = plan.GetResourceChange(Tfplan.Action.Update);
            var destroyResources = plan.GetResourceChange(Tfplan.Action.Delete);
            var recreateResources = plan.GetResourceChange(Tfplan.Action.DeleteThenCreate);

            taskContext.LogInformation(
                $"Plan summary:\n- Unchanged: {unchangedResourcesCount}\n- Create:    {createResourcesCount}\n- Modify:    {modifyResourcesCount}\n- Destroy:   {destroyResourcesCount}\n- Recreate:  {recreateResourcesCount}\n- Count Before Apply:  {totalResourcesCountBefore}\n- Count After Apply:   {totalResourcesCountAfter}");

            // Extract output counts and changes
            var unchangedOutputsCount = plan.GetOutputCount(Tfplan.Action.Noop);
            var createOutputsCount = plan.GetOutputCount(Tfplan.Action.Create);
            var modifyOutputsCount = plan.GetOutputCount(Tfplan.Action.Update);
            var destroyOutputsCount = plan.GetOutputCount(Tfplan.Action.Delete);
            var recreateOutputsCount = plan.GetOutputCount(Tfplan.Action.DeleteThenCreate);
            var totalChangedOutputsCount = createOutputsCount + modifyOutputsCount + destroyOutputsCount + recreateOutputsCount;

            var unchangedOutputs = plan.GetOutputChange(Tfplan.Action.Noop);
            var createOutputs = plan.GetOutputChange(Tfplan.Action.Create);
            var modifyOutputs = plan.GetOutputChange(Tfplan.Action.Update);
            var destroyOutputs = plan.GetOutputChange(Tfplan.Action.Delete);
            var recreateOutputs = plan.GetOutputChange(Tfplan.Action.DeleteThenCreate);

            taskContext.LogInformation(
                $"Plan summary:\n- Unchanged Outputs: {unchangedOutputsCount}\n- Create Outputs:    {createOutputsCount}\n- Modify Outputs:    {modifyOutputsCount}\n- Destroyed Outputs: {destroyOutputsCount}\n- Recreate Outputs:  {recreateOutputsCount}");

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
                Unchanged = JsonSerializer.Serialize(unchangedResources),
                Create = JsonSerializer.Serialize(createResources),
                Modify = JsonSerializer.Serialize(modifyResources),
                Destroy = JsonSerializer.Serialize(destroyResources),
                Recreate = JsonSerializer.Serialize(recreateResources),
                OutputsTotalCount = totalChangedOutputsCount + unchangedOutputsCount,
                OutputsTotalChangedCount = totalChangedOutputsCount,
                OutputsTotalUnchangedCount = unchangedOutputsCount,
                OutputsCreateCount = createOutputsCount,
                OutputsModifyCount = modifyOutputsCount,
                OutputsDestroyCount = destroyOutputsCount,
                OutputsRecreateCount = recreateOutputsCount,
                OutputsUnchanged = JsonSerializer.Serialize(unchangedOutputs),
                OutputsCreate = JsonSerializer.Serialize(createOutputs),
                OutputsModify = JsonSerializer.Serialize(modifyOutputs),
                OutputsDestroy = JsonSerializer.Serialize(destroyOutputs),
                OutputsRecreate = JsonSerializer.Serialize(recreateOutputs)
            };

            await InvokeWithRetryAsync(
                () => runnerHubClient.InvokePlanCompleted(request.JobId, planData),
                nameof(runnerHubClient.InvokePlanCompleted),
                request.JobId,
                connection);

            taskContext.LogInformation("Completed Plan");
        }
        catch (OperationCanceledException)
        {
            taskContext.LogWarning("Plan process was cancelled.");
            await InvokeWithRetryAsync(
                () => runnerHubClient.InvokePlanCancelled(request.JobId),
                nameof(runnerHubClient.InvokePlanCancelled),
                request.JobId,
                connection);
        }
        catch (Exception ex)
        {
            taskContext.LogError($"Error handling Plan for job {request.JobId}. {ex.Message}");
            await InvokeWithRetryAsync(
                () => runnerHubClient.InvokePlanFaulted(
                    request.JobId,
                    ex.Message,
                    ex.StackTrace
                ),
                nameof(runnerHubClient.InvokePlanFaulted),
                request.JobId,
                connection);
        }
        finally
        {
            // Stop periodic reporting
            reportingCts?.Cancel();
            if (reportingTask != null)
                try
                {
                    await reportingTask;
                }
                catch
                {
                    /* Already logged */
                }

            _processRegistry.Remove(request.JobId, CancellationType.ImmediateKill);
            _processRegistry.Remove(request.JobId, CancellationType.ImmediateGraceful);
        }
    }
}