using Newtonsoft.Json.Linq;
using Action = Tfplan.Action;


namespace SnapCd.Runner.Services.Plan;

public class ParsedPlan
{
    public required Tfplan.Plan Plan { get; set; }
    public required JObject State { get; set; }

    public int GetExistingCount()
    {
        if (State.TryGetValue("resources", out var resourcesToken) && resourcesToken is JArray resourcesArray) return resourcesArray.Count;

        return 0;
    }

    public int GetResourceCount(Action action)
    {
        var count = Plan.ResourceChanges.Count(rc => rc.Change.Action == action);
        return count;
    }

    public int GetOutputCount(Action action)
    {
        var count = Plan.OutputChanges.Count(rc => rc.Change.Action == action);
        return count;
    }

    public List<Tfplan.ResourceInstanceChange> GetResourceChange(Action action)
    {
        var change = Plan.ResourceChanges.Where(rc => rc.Change.Action == action).ToList();
        return change;
    }

    public List<Tfplan.OutputChange> GetOutputChange(Action action)
    {
        var change = Plan.OutputChanges.Where(rc => rc.Change.Action == action).ToList();
        return change;
    }
}