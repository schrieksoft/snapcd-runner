using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SnapCd.Common;
using SnapCd.Common.Dto;
using SnapCd.Common.Dto.Outputs;
using SnapCd.Common.Dto.OutputSets;
// ParamResolver related using statements removed - no longer needed on runner
using SnapCd.Common.RunnerRequests;
using SnapCd.Common.RunnerRequests.HelperClasses;
using SnapCd.Runner.Exceptions;
using SnapCd.Runner.Services.Plan;
using SnapCd.Runner.Utils;
using File = System.IO.File;

namespace SnapCd.Runner.Services;

public static class NativeMethods
{
    // Import the native kill function from libc.
    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int sig);

    // public const int Sigterm = 15; // termination signal

    public const int Sigint = 2; // termination signal
}

public class Engine
{
    private readonly TaskContext _context;
    private readonly ILogger<Engine> _logger;
    private readonly string _snapCdDir;
    private readonly string _initDir;
    private readonly string _engine;

    private const string PlanEntryName = "tfplan";

    private const string StateEntryName = "tfstate";
    // private const string PreviousStateEntryName = "tfstate-prev";
    // private const ulong FormatVersion = 3;


    private Dictionary<string, string> _envVars = new();

    public Engine(
        TaskContext context,
        ILogger<Engine> logger,
        ModuleDirectoryService moduleDirectoryService,
        string engine
    )
    {
        _logger = logger;
        _context = context;
        _engine = engine;
        _snapCdDir = moduleDirectoryService.GetSnapCdDir();
        _initDir = moduleDirectoryService.GetInitDir();

        // Try to load existing environment variables from file
        LoadEnvVarsFromFile();
    }

    # region commands

    public string GetInitDir()
    {
        return _initDir;
    }

    public string GetSnapCdDir()
    {
        return _snapCdDir;
    }

    public ParsedPlan ParseDestroyPlan()
    {
        return ParsePlan(GetPlanDestroyPath());
    }

    public ParsedPlan ParseApplyPlan()
    {
        return ParsePlan(GetPlanApplyPath());
    }

    private ParsedPlan ParsePlan(string planPath)
    {
        using var archive = ZipFile.OpenRead(planPath);
        var entry = archive.GetEntry(PlanEntryName)
                    ?? throw new InvalidDataException($"Missing {PlanEntryName} entry in archive");

        using (var entryStream = entry.Open())
        {
            var plan = Tfplan.Plan.Parser.ParseFrom(entryStream);
            var state = ParseState(archive);
            return new ParsedPlan
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
        // Use pre-resolved environment variables from server
        _envVars = resolvedEnvVars;
        SaveEnvVarsToFile();

        var initFlags = new List<string>();

        // Add flags based on auto settings
        if (flags.AutoUpgradeEnabled)
            initFlags.Add("-upgrade");

        if (flags.AutoReconfigureEnabled) initFlags.Add("-reconfigure");
        if (flags.AutoMigrateEnabled) initFlags.Add("-migrate-state");

        var baseScript = $"{_engine} init {string.Join(" ", initFlags)}";

        var backendConfigArgs = BuildBackendConfigArgs(backendConfig);
        if (!string.IsNullOrWhiteSpace(backendConfigArgs))
            baseScript += $" {backendConfigArgs}";

        var script = await CreateScriptAsync(
            baseScript,
            beforeHook,
            afterHook,
            killCancellationToken);

        // write out script to directory from which it is executed (not this is not needed for RunProcess, but so that users could manually run it if needed)
        await File.WriteAllTextAsync($"{_snapCdDir}/init.sh", script);

        return await RunProcess(script, killCancellationToken, gracefulCancellationToken);
    }

    private string BuildBackendConfigArgs(EngineBackendConfiguration backend)
    {
        var backendConfigs = new Dictionary<string, string>();

        // Start with namespace backend configs if not ignored
        if (!backend.IgnoreNamespaceBackendConfigs)
            foreach (var config in backend.NamespaceBackendConfigs)
                backendConfigs[config.Name] = config.Value;

        // Override with module backend configs
        foreach (var config in backend.ModuleBackendConfigs) backendConfigs[config.Name] = config.Value;

        // Build the backend config arguments
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
                $"{_engine} validation failed in directory {_initDir}",
                ex,
                _initDir,
                -1,
                ex.Message);
        }
    }


    public async Task<int> Statistics(CancellationToken killCancellationToken = default, CancellationToken gracefulCancellationToken = default)
    {
        var resources = await RunProcess($"{_engine} state list", killCancellationToken, gracefulCancellationToken);

        // Count non-empty lines, excluding lines that start with "data."
        var lines = resources.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.Trim().StartsWith("data."))
            .ToArray();
        return lines.Length;
    }

    public async Task<int> ReadStatisticsFromFile()
    {
        var statisticsFilePath = $"{_snapCdDir}/statistics.txt";

        if (!File.Exists(statisticsFilePath))
        {
            _context.LogWarning("Statistics file not found at {Path}", statisticsFilePath);
            return 0;
        }

        try
        {
            var content = await File.ReadAllTextAsync(statisticsFilePath);
            if (int.TryParse(content.Trim(), out var count)) return count;

            _context.LogWarning("Unable to parse statistics from file: {Content}", content);
            return 0;
        }
        catch (Exception ex)
        {
            _context.LogError("Error reading statistics file: {Error}", ex.Message);
            return 0;
        }
    }


    public async Task<string> Plan(
        Dictionary<string, string> parameters,
        string? planBeforeHook,
        string? planAfterHook,
        CancellationToken killCancellationToken = default,
        CancellationToken gracefulCancellationToken = default)
    {
        // Convert dictionary to tfvars string format
        var tfVarsString = string.Join("", parameters.Select(kvp => $"{kvp.Key}={kvp.Value}\n"));

        // Use pre-resolved variables from server
        var tfvarsPath = GetTfVarsPath();
        await File.WriteAllTextAsync(tfvarsPath, tfVarsString);

        var beforeHook = planBeforeHook;
        var afterHook = planAfterHook;

        var script = await CreateScriptAsync(
            $"{_engine} plan -out={GetPlanApplyPath()} -input=false -var-file={tfvarsPath}",
            beforeHook,
            afterHook,
            killCancellationToken);

        await File.WriteAllTextAsync($"{_snapCdDir}/plan.sh", script);

        return await RunProcess(script, killCancellationToken, gracefulCancellationToken);
    }


    public async Task<string> PlanDestroy(
        Dictionary<string, string> parameters,
        string? beforeHook,
        string? afterHook,
        CancellationToken killCancellationToken = default,
        CancellationToken gracefulCancellationToken = default)
    {
        // Convert dictionary to tfvars string format
        var tfVarsString = string.Join("", parameters.Select(kvp => $"{kvp.Key}={kvp.Value}\n"));

        // Use pre-resolved variables from server
        var tfvarsPath = GetTfVarsPath();
        await File.WriteAllTextAsync(tfvarsPath, tfVarsString);

        var script = await CreateScriptAsync(
            $"{_engine} plan -destroy -out={GetPlanDestroyPath()} -input=false -var-file={tfvarsPath}",
            beforeHook,
            afterHook,
            killCancellationToken);

        await File.WriteAllTextAsync($"{_snapCdDir}/plan_destroy.sh", script);

        return await RunProcess(script, killCancellationToken, gracefulCancellationToken);
    }

    public async Task<string> DestroyFromPlan(
        string? beforeHook,
        string? afterHook,
        CancellationToken killCancellationToken = default,
        CancellationToken gracefulCancellationToken = default)
    {
        // Create main destroy command with statistics collection at the end
        var mainCommand = $"{_engine} apply {GetPlanDestroyPath()}\n{_engine} state list | grep -v '^data\\.' | wc -l > {_snapCdDir}/statistics.txt";

        var script = await CreateScriptAsync(
            mainCommand,
            beforeHook,
            afterHook,
            killCancellationToken);

        await File.WriteAllTextAsync($"{_snapCdDir}/destroy.sh", script);

        return await RunProcess(script, killCancellationToken, gracefulCancellationToken);
    }

    public async Task<string> ApplyFromPlan(
        string? beforeHook,
        string? afterHook,
        CancellationToken killCancellationToken = default,
        CancellationToken gracefulCancellationToken = default)
    {
        // Create main apply command with statistics collection at the end
        var mainCommand = $"{_engine} apply {GetPlanApplyPath()}\n{_engine} state list | grep -v '^data\\.' | wc -l > {_snapCdDir}/statistics.txt";

        var script = await CreateScriptAsync(
            mainCommand,
            beforeHook,
            afterHook,
            killCancellationToken);

        await File.WriteAllTextAsync($"{_snapCdDir}/apply.sh", script);

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

        await File.WriteAllTextAsync($"{_snapCdDir}/output.sh", script);

        await RunProcess(script, killCancellationToken, gracefulCancellationToken);

        using var reader = new StreamReader($"{_snapCdDir}/output.json");
        var output = reader.ReadToEnd();

        return output;
    }

    # endregion


    # region cmd

    public async Task<string> RunProcess(string script, CancellationToken killCancellationToken, CancellationToken gracefulCancellationToken)
    {
        // Ensure environment variables are loaded before running any process
        EnsureEnvVarsLoaded();

        var arguments = $"-c \"{StringFormatting.EscapeBashScript(script)}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _initDir
        };

        foreach (var envVar in _envVars)
            startInfo.EnvironmentVariables[envVar.Key] = envVar.Value;

        var process = new Process { StartInfo = startInfo };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();


        gracefulCancellationToken.Register(() =>
        {
            if (!process.HasExited)
            {
                // Using the non-generic NativeMethods class to send SIGTERM
                var result = NativeMethods.kill(process.Id, NativeMethods.Sigint);

                if (result == 0)
                    _context.LogInformation("Sent SIGINT to process for graceful termination.");
                else
                    _context.LogError("Failed to send SIGINT. Process might already be terminated or access is denied.");
            }
        });

        killCancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    _context.LogInformation($"Process killed via cancellation.");
                }
            }
            catch (Exception ex)
            {
                _context.LogError($"Error during process kill: {ex.Message}");
            }
        });

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _context.LogInformation(e.Data);
                outputBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                errorBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // await process.WaitForExitAsync(killCancellationToken);
        // await process.WaitForExitAsync(gracefulCancellationToken);

        using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                   killCancellationToken,
                   gracefulCancellationToken))
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }

        var error = errorBuilder.ToString();
        if (process.ExitCode != 0 || error != "")
        {
            _context.LogError($"Failed with error: \n{error}");
            throw new Exception($"Process in {_initDir} failed. \n {error}");
        }

        return outputBuilder.ToString();
    }

    # endregion


    # region paths

    private string GetTfVarsPath()
    {
        return $"{_snapCdDir}/inputs.tfvars";
    }

    private string GetPlanApplyPath()
    {
        return $"{_snapCdDir}/plan.out";
    }

    private string GetPlanDestroyPath()
    {
        return $"{_snapCdDir}/destroy.out";
    }

    # endregion


    # region inputs and outputs

    private string CalculateChecksum(string input)
    {
        using (var sha256 = SHA256.Create())
        {
            var inputBytes = Encoding.UTF8.GetBytes(input);
            var hashBytes = sha256.ComputeHash(inputBytes);

            // Convert the byte array to a hexadecimal string
            var sb = new StringBuilder();
            foreach (var b in hashBytes) sb.Append(b.ToString("x2"));

            return sb.ToString();
        }
    }

    // ResolveEnvVars method removed - environment variables are now pre-resolved on the server

    private void SaveEnvVarsToFile()
    {
        var exportLines = _envVars
            .Select(envVar => $"export {envVar.Key}={System.Text.Json.JsonSerializer.Serialize(envVar.Value)}");

        File.WriteAllText($"{_snapCdDir}/snapcd.env", string.Join(Environment.NewLine, exportLines));

        _context.LogInformation("Environment Variables saved to file");
    }

    private bool LoadEnvVarsFromFile()
    {
        var envFilePath = $"{_snapCdDir}/snapcd.env";
        if (!File.Exists(envFilePath)) return false;

        try
        {
            var lines = File.ReadAllLines(envFilePath);
            _envVars = new Dictionary<string, string>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("export "))
                    continue;

                var exportLine = line.Substring("export ".Length);
                var equalsIndex = exportLine.IndexOf('=');
                if (equalsIndex < 0)
                    continue;

                var key = exportLine.Substring(0, equalsIndex);
                var valueJson = exportLine.Substring(equalsIndex + 1);

                // Deserialize the JSON value
                var value = System.Text.Json.JsonSerializer.Deserialize<string>(valueJson);
                if (value != null) _envVars[key] = value;
            }

            _context.LogInformation("Environment Variables loaded from file");
            return true;
        }
        catch (Exception ex)
        {
            _context.LogWarning($"Failed to load environment variables from file: {ex.Message}");
            return false;
        }
    }

    private void EnsureEnvVarsLoaded()
    {
        // If we already have env vars, we're good
        if (_envVars.Count > 0) return;

        // Try to load from file - this must exist (created by Init)
        if (!LoadEnvVarsFromFile())
            throw new InvalidOperationException(
                "Environment variables file not found. Init must be run first to resolve and store environment variables.");
    }


    public async Task<string> CreateScriptAsync(string baseScript, string? beforeHook, string? afterHook, CancellationToken cancellationToken = default)
    {
        var beforeHookMessage = "Now running before hook";
        if (string.IsNullOrEmpty(beforeHook))
            beforeHookMessage = "No before hook defined. Skipping.";

        var afterHookMessage = "Now running after hook";
        if (string.IsNullOrEmpty(afterHook))
            afterHookMessage = "No after hook defined. Skipping.";

        var script = @$"
echo "">>>>>>>> {beforeHookMessage} <<<<<<<<<""
{beforeHook}

echo "">>>>>>>> Now running main script <<<<<<<<<""
{baseScript}

echo "">>>>>>>> {afterHookMessage} <<<<<<<<<""
{afterHook}
";
        return script;
    }


    public async Task<OutputSetCreateDto?> ParseJsonToModuleOutputSet(string json, Dictionary<string, bool>? outputSources = null)
    {
        // Parse the JSON string into a JObject
        var jsonObject = JObject.Parse(json);

        // Always create an OutputSet, even for empty outputs (when json == "{}")
        // This handles both cases: module transitions to no outputs, and module destroy

        // Initialize a new ModuleOutput object
        var moduleOutputSet = new OutputSetCreateDto
        {
            Checksum = CalculateChecksum(json),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Outputs = new List<OutputCreateDto>()
        };

        var exceptions = new List<Exception>();

        // Process each property in the JSON object in parallel
        await Task.WhenAll(jsonObject.Properties().Select(property => Task.Run(() =>
        {
            try
            {
                // Determine the type
                string? type = null;
                if (property.Value["type"] is JArray typeArray && typeArray.Count > 0)
                    type = typeArray[0].ToString();
                else if (property.Value["type"] != null) type = property.Value["type"]?.ToString();

                // Determine the value
                string? value = null;
                if (property.Value["value"] is JObject valueJObject)
                    value = valueJObject.ToString(Formatting.None); // Serialize as a compact string
                else if (property.Value["value"] != null) value = property.Value["value"]?.ToString();

                // Determine if output is from an extra file
                var fromExtraFile = outputSources != null &&
                                    outputSources.TryGetValue(property.Name, out var isExtra) &&
                                    isExtra;

                // Create a ModuleOutput object for each JSON property
                var moduleOutput = new OutputReadDto
                {
                    Name = property.Name,
                    Sensitive = property.Value["sensitive"]?.Value<bool>(),
                    Type = type ?? throw new InvalidOperationException("Output type is not defined"),
                    Value = value ?? throw new InvalidOperationException("Output value is not defined"),
                    FromExtraFile = fromExtraFile
                };

                // Add the ModuleOutput object to the list in ModuleOutputSet
                lock (moduleOutputSet.Outputs) // Lock to ensure thread safety
                {
                    moduleOutputSet.Outputs.Add(moduleOutput);
                }
            }
            catch (Exception ex)
            {
                // Collect the exception with detailed context
                var e = new Exception(
                    $"Error processing JSON property '{property.Name}'", ex);

                exceptions.Add(e);

                _context.LogError($"Error processing JSON property '{property.Name}'. Error: \n {ex}");
            }
        })));

        // After processing all tasks, throw if there were exceptions
        if (exceptions.Any())
            throw new AggregateException("One or more errors occurred while processing JSON properties.", exceptions);

        return moduleOutputSet;
    }

    # endregion
}