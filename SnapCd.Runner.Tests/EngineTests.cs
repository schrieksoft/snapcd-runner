using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SnapCd.Common;
using SnapCd.Common.Dto.ModuleBackendConfigs;
using SnapCd.Common.Dto.NamespaceBackendConfigs;
using SnapCd.Common.RunnerRequests;
using SnapCd.Common.RunnerRequests.HelperClasses;
using SnapCd.Runner.Services;
using SnapCd.Runner.Settings;

namespace SnapCd.Runner.Tests;

public class EngineTests : IDisposable
{
    private readonly Mock<ILogger<Engine>> _mockLogger;
    private readonly TaskContext _taskContext;
    private readonly string _testWorkingDirectory;
    private readonly ModuleDirectoryService _moduleDirectoryService;
    private readonly Engine _engine;
    private const string TestEngine = "terraform";

    public EngineTests()
    {
        // Create a unique test directory for each test run
        _testWorkingDirectory = Path.Combine(
            Path.GetTempPath(),
            "SnapCdRunnerTests",
            Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testWorkingDirectory);

        // Setup mocks
        _mockLogger = new Mock<ILogger<Engine>>();
        var mockTaskLogger = new Mock<ILogger>();

        var metadata = new JobMetadata
        {
            ModuleName = "test-module",
            NamespaceName = "test-namespace",
            StackName = "test-stack",
            ModuleId = Guid.NewGuid(),
            SourceSubdirectory = null
        };

        _taskContext = new TaskContext(
            Guid.NewGuid(),
            "EngineTests",
            mockTaskLogger.Object,
            metadata
        );

        // Setup module directory service with our test directory
        var workingDirectorySettings = Options.Create(new WorkingDirectorySettings
        {
            WorkingDirectory = _testWorkingDirectory,
            TempDirectory = Path.Combine(_testWorkingDirectory, "temp")
        });

        _moduleDirectoryService = new ModuleDirectoryService(
            metadata,
            workingDirectorySettings);

        // Create the engine instance
        _engine = new Engine(
            _taskContext,
            _mockLogger.Object,
            _moduleDirectoryService,
            TestEngine
        );

        // Create required directories
        Directory.CreateDirectory(_engine.GetInitDir());
        Directory.CreateDirectory(_engine.GetSnapCdDir());

        // Create an empty environment variables file so Plan/PlanDestroy don't fail
        var envFilePath = Path.Combine(_engine.GetSnapCdDir(), "snapcd.env");
        File.WriteAllText(envFilePath, "# Empty env file for testing");
    }

    public void Dispose()
    {
        // Cleanup test directory
        try
        {
            if (Directory.Exists(_testWorkingDirectory))
            {
                Directory.Delete(_testWorkingDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void Constructor_ShouldInitializeDirectories()
    {
        // Assert
        Assert.NotNull(_engine.GetInitDir());
        Assert.NotNull(_engine.GetSnapCdDir());
        Assert.True(Directory.Exists(_engine.GetInitDir()));
        Assert.True(Directory.Exists(_engine.GetSnapCdDir()));
    }

    [Fact]
    public async Task Plan_ShouldConvertDictionaryToTfVarsFile()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            { "test_variable", "\"test_value\"" },
            { "count_variable", "5" },
            { "bool_variable", "true" }
        };

        // Copy the SimpleModule fixture to the init directory
        var fixtureSource = Path.Combine(
            Directory.GetCurrentDirectory(),
            "Fixtures",
            "TerraformModules",
            "SimpleModule");

        CopyDirectory(fixtureSource, _engine.GetInitDir());

        // Act & Assert - This will fail if terraform is not installed,
        // but it will succeed in writing the tfvars file
        try
        {
            await _engine.Plan(parameters, null, null);
        }
        catch (Exception ex) when (ex.Message.Contains("terraform"))
        {
            // Expected if terraform not installed - that's OK, we're testing file writing
        }

        // Verify tfvars file was created with correct content
        var tfvarsPath = Path.Combine(_engine.GetSnapCdDir(), "inputs.tfvars");
        Assert.True(File.Exists(tfvarsPath), "tfvars file should be created");

        var tfvarsContent = await File.ReadAllTextAsync(tfvarsPath);
        Assert.Contains("test_variable=\"test_value\"", tfvarsContent);
        Assert.Contains("count_variable=5", tfvarsContent);
        Assert.Contains("bool_variable=true", tfvarsContent);
    }

    [Fact]
    public async Task PlanDestroy_ShouldConvertDictionaryToTfVarsFile()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            { "var1", "\"value1\"" },
            { "var2", "\"value2\"" }
        };

        // Copy the SimpleModule fixture to the init directory
        var fixtureSource = Path.Combine(
            Directory.GetCurrentDirectory(),
            "Fixtures",
            "TerraformModules",
            "SimpleModule");

        CopyDirectory(fixtureSource, _engine.GetInitDir());

        // Act & Assert
        try
        {
            await _engine.PlanDestroy(parameters, null, null);
        }
        catch (Exception ex) when (ex.Message.Contains("terraform"))
        {
            // Expected if terraform not installed
        }

        // Verify tfvars file was created
        var tfvarsPath = Path.Combine(_engine.GetSnapCdDir(), "inputs.tfvars");
        Assert.True(File.Exists(tfvarsPath));

        var tfvarsContent = await File.ReadAllTextAsync(tfvarsPath);
        Assert.Contains("var1=\"value1\"", tfvarsContent);
        Assert.Contains("var2=\"value2\"", tfvarsContent);
    }

    [Fact]
    public async Task Plan_ShouldCreatePlanScriptFile()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            { "test_var", "\"test\"" }
        };

        // Copy fixture
        var fixtureSource = Path.Combine(
            Directory.GetCurrentDirectory(),
            "Fixtures",
            "TerraformModules",
            "SimpleModule");

        CopyDirectory(fixtureSource, _engine.GetInitDir());

        // Act
        try
        {
            await _engine.Plan(parameters, null, null);
        }
        catch
        {
            // Expected if terraform not installed
        }

        // Assert - verify script file was created
        var scriptPath = Path.Combine(_engine.GetSnapCdDir(), "plan.sh");
        Assert.True(File.Exists(scriptPath), "plan.sh script should be created");

        var scriptContent = await File.ReadAllTextAsync(scriptPath);
        Assert.Contains("terraform plan", scriptContent);
        Assert.Contains("inputs.tfvars", scriptContent);
    }

    [Fact]
    public async Task Plan_WithHooks_ShouldIncludeHooksInScript()
    {
        // Arrange
        var parameters = new Dictionary<string, string>();
        const string beforeHook = "echo 'Before plan'";
        const string afterHook = "echo 'After plan'";

        // Copy fixture
        var fixtureSource = Path.Combine(
            Directory.GetCurrentDirectory(),
            "Fixtures",
            "TerraformModules",
            "SimpleModule");

        CopyDirectory(fixtureSource, _engine.GetInitDir());

        // Act
        try
        {
            await _engine.Plan(parameters, beforeHook, afterHook);
        }
        catch
        {
            // Expected if terraform not installed
        }

        // Assert
        var scriptPath = Path.Combine(_engine.GetSnapCdDir(), "plan.sh");
        Assert.True(File.Exists(scriptPath));

        var scriptContent = await File.ReadAllTextAsync(scriptPath);
        Assert.Contains(beforeHook, scriptContent);
        Assert.Contains(afterHook, scriptContent);
    }

    public async Task Plan_WithTerraformInstalled_ShouldExecuteSuccessfully()
    {
        // This test is skipped by default and can be run manually when terraform is available
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            { "test_message", "\"integration test\"" }
        };

        var fixtureSource = Path.Combine(
            Directory.GetCurrentDirectory(),
            "Fixtures",
            "TerraformModules",
            "SimpleModule");

        CopyDirectory(fixtureSource, _engine.GetInitDir());

        // First init
        await _engine.Init(
            new Dictionary<string, string>(),
            null,
            null,
            new EngineBackendConfiguration
            {
                IgnoreNamespaceBackendConfigs = true,
                NamespaceBackendConfigs = new List<NamespaceBackendConfigReadDto>(),
                ModuleBackendConfigs = new List<ModuleBackendConfigReadDto>()
            },
            new EngineFlags
            {
                AutoUpgradeEnabled = false,
                AutoReconfigureEnabled = false,
                AutoMigrateEnabled = false
            });

        // Act
        var result = await _engine.Plan(parameters, null, null);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");
        }

        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetDirectoryName(subDir)!);
            CopyDirectory(subDir, destSubDir);
        }
    }
}
