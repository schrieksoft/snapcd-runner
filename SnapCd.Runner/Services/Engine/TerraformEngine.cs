using System.IO.Compression;
using Newtonsoft.Json.Linq;
using SnapCd.Common;
using SnapCd.Common.Dto.OutputSets;
using SnapCd.Common.RunnerRequests.HelperClasses;
using SnapCd.Runner.Exceptions;
using SnapCd.Runner.Services.Plan;
using File = System.IO.File;

namespace SnapCd.Runner.Services;

public class TerraformEngine : BaseEngine, IEngine
{
    private readonly string _engine;

    private const string PlanEntryName = "tfplan";
    private const string StateEntryName = "tfstate";

    public TerraformEngine(
        TaskContext context,
        ILogger logger,
        ModuleDirectoryService moduleDirectoryService,
        string engine,
        List<string> additionalBinaryPaths
    ) : base(context, logger, moduleDirectoryService, additionalBinaryPaths)
    {
        _engine = engine;
    }

    public IParsedPlan ParseDestroyPlan()
    {
        return ParsePlan(GetPlanDestroyPath());
    }

    public IParsedPlan ParseApplyPlan()
    {
        return ParsePlan(GetPlanApplyPath());
    }

    private TerraformParsedPlan ParsePlan(string planPath)
    {
        using var archive = ZipFile.OpenRead(planPath);
        var entry = archive.GetEntry(PlanEntryName)
                    ?? throw new InvalidDataException($"Missing {PlanEntryName} entry in archive");

        using (var entryStream = entry.Open())
        {
            var plan = Tfplan.Plan.Parser.ParseFrom(entryStream);
            var state = ParseState(archive);
            return new TerraformParsedPlan
            {
                Plan = plan,
                State = state
            };
        }
    }

    private JObject ParseState(ZipArchive archive)
    {
        var entry = archive.GetEntry(StateEntryName)
                    ?? throw new InvalidDataException($"Missing {StateEntryName} entry in archive");

        using (var entryStream = entry.Open())
        using (var reader = new StreamReader(entryStream))
        {
            var content = reader.ReadToEnd();
            return JObject.Parse(content);
        }
    }

    public async Task<string> Init(
        Dictionary<string, string> resolvedEnvVars,
        string? beforeHook,
        string? afterHook,
        EngineBackendConfiguration backendConfig,
        EngineFlags flags,
        CancellationToken killCancellationToken = default,
        CancellationToken gracefulCancellationToken = default)
    {
        EnvVars = resolvedEnvVars;
        SaveEnvVarsToFile();

        var initFlags = new List<string>();

        if (flags.AutoUpgradeEnabled)
            initFlags.Add("-upgrade");

        if (flags.AutoReconfigureEnabled) initFlags.Add("-reconfigure");
        if (flags.AutoMigrateEnabled) initFlags.Add("-migrate-state");

        var initCommand = $"{_engine} init {string.Join(" ", initFlags)}";
        string baseScript;
        if (flags.AutoMigrateEnabled)
            baseScript = $"echo \"yes\" | {initCommand}";
        else if (flags.AutoReconfigureEnabled)
            baseScript = $"echo \"no\" | {initCommand}";
        else
            baseScript = initCommand;

        var backendConfigArgs = BuildBackendConfigArgs(backendConfig);
        if (!string.IsNullOrWhiteSpace(backendConfigArgs))
            baseScript += $" {backendConfigArgs}";

        var script = await CreateScriptAsync(
            baseScript,
            beforeHook,
            afterHook,
            killCancellationToken);

        await File.WriteAllTextAsync($"{SnapCdDir}/init.sh", script);

        return await RunProcess(script, killCancellationToken, gracefulCancellationToken);
    }

    private string BuildBackendConfigArgs(EngineBackendConfiguration backend)
    {
        var backendConfigs = new Dictionary<string, string>();

        if (!backend.IgnoreNamespaceBackendConfigs)
            foreach (var config in backend.NamespaceBackendConfigs)
                backendConfigs[config.Name] = config.Value;

        foreach (var config in backend.ModuleBackendConfigs) backendConfigs[config.Name] = config.Value;

        var args = new List<string>();
        foreach (var kvp in backendConfigs) args.Add($"-backend-config=\"{kvp.Key}={kvp.Value}\"");

        return string.Join(" ", args);
    }

    public async Task Validate(
        string? beforeHook = null,
        string? afterHook = null,
        CancellationToken killCancellationToken = default,
        CancellationToken gracefulCancellationToken = default)
    {
        var baseScript = $"{_engine} validate";

        var script = await CreateScriptAsync(
            baseScript,
            beforeHook,
            afterHook,
            killCancellationToken);

        try
        {
            await RunProcess(script, killCancellationToken, gracefulCancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EngineValidationException(
                $"{_engine} validation failed in directory {InitDir}",
                ex,
                InitDir,
                -1,
                ex.Message);
        }
    }

    public async Task<int> Statistics(CancellationToken killCancellationToken = default, CancellationToken gracefulCancellationToken = default)
    {
        var resources = await RunProcess($"{_engine} state list", killCancellationToken, gracefulCancellationToken);

        var lines = resources.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.Trim().StartsWith("data."))
            .ToArray();
        return lines.Length;
    }

    public async Task<string> Plan(
        Dictionary<string, string> parameters,
        string? planBeforeHook,
        string? planAfterHook,
        CancellationToken killCancellationToken = default,
        CancellationToken gracefulCancellationToken = default)
    {
        var tfVarsString = string.Join("", parameters.Select(kvp => $"{kvp.Key}={kvp.Value}\n"));

        var tfvarsPath = GetTfVarsPath();
        await File.WriteAllTextAsync(tfvarsPath, tfVarsString);

        var script = await CreateScriptAsync(
            $"{_engine} plan -out={GetPlanApplyPath()} -input=false -var-file={tfvarsPath}",
            planBeforeHook,
            planAfterHook,
            killCancellationToken);

        await File.WriteAllTextAsync($"{SnapCdDir}/plan.sh", script);

        return await RunProcess(script, killCancellationToken, gracefulCancellationToken);
    }

    public async Task<string> PlanDestroy(
        Dictionary<string, string> parameters,
        string? beforeHook,
        string? afterHook,
        CancellationToken killCancellationToken = default,
        CancellationToken gracefulCancellationToken = default)
    {
        var tfVarsString = string.Join("", parameters.Select(kvp => $"{kvp.Key}={kvp.Value}\n"));

        var tfvarsPath = GetTfVarsPath();
        await File.WriteAllTextAsync(tfvarsPath, tfVarsString);

        var script = await CreateScriptAsync(
            $"{_engine} plan -destroy -out={GetPlanDestroyPath()} -input=false -var-file={tfvarsPath}",
            beforeHook,
            afterHook,
            killCancellationToken);

        await File.WriteAllTextAsync($"{SnapCdDir}/plan_destroy.sh", script);

        return await RunProcess(script, killCancellationToken, gracefulCancellationToken);
    }

    public async Task<string> DestroyFromPlan(
        string? beforeHook,
        string? afterHook,
        CancellationToken killCancellationToken = default,
        CancellationToken gracefulCancellationToken = default)
    {
        var mainCommand = $"{_engine} apply {GetPlanDestroyPath()}\n{_engine} state list | grep -v '^data\\.' | wc -l > {SnapCdDir}/statistics.txt";

        var script = await CreateScriptAsync(
            mainCommand,
            beforeHook,
            afterHook,
            killCancellationToken);

        await File.WriteAllTextAsync($"{SnapCdDir}/destroy.sh", script);

        return await RunProcess(script, killCancellationToken, gracefulCancellationToken);
    }

    public async Task<string> ApplyFromPlan(
        string? beforeHook,
        string? afterHook,
        CancellationToken killCancellationToken = default,
        CancellationToken gracefulCancellationToken = default)
    {
        var mainCommand = $"{_engine} apply {GetPlanApplyPath()}\n{_engine} state list | grep -v '^data\\.' | wc -l > {SnapCdDir}/statistics.txt";

        var script = await CreateScriptAsync(
            mainCommand,
            beforeHook,
            afterHook,
            killCancellationToken);

        await File.WriteAllTextAsync($"{SnapCdDir}/apply.sh", script);

        return await RunProcess(script, killCancellationToken, gracefulCancellationToken);
    }

    public async Task<string> Output(
        string? beforeHook,
        string? afterHook,
        CancellationToken killCancellationToken = default,
        CancellationToken gracefulCancellationToken = default)
    {
        var script = await CreateScriptAsync(
            $"{_engine} output -json > .snapcd/output.json",
            beforeHook,
            afterHook,
            killCancellationToken);

        await File.WriteAllTextAsync($"{SnapCdDir}/output.sh", script);

        await RunProcess(script, killCancellationToken, gracefulCancellationToken);

        using var reader = new StreamReader($"{SnapCdDir}/output.json");
        var output = reader.ReadToEnd();

        return output;
    }

    private string GetTfVarsPath() => $"{SnapCdDir}/inputs.tfvars";
    private string GetPlanApplyPath() => $"{SnapCdDir}/plan.out";
    private string GetPlanDestroyPath() => $"{SnapCdDir}/destroy.out";
}
