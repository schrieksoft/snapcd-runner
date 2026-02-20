using Microsoft.Extensions.Options;
using SnapCd.Common;
using SnapCd.Common.RunnerRequests;
using SnapCd.Common.RunnerRequests.HelperClasses;
using SnapCd.Runner.Services;
using SnapCd.Runner.Settings;

namespace SnapCd.Runner.Factories;

public class EngineFactory
{
    private readonly IOptions<WorkingDirectorySettings> _workingDirectorySettings;
    private readonly IOptions<EngineSettings> _engineSettings;
    private readonly ILoggerFactory _loggerFactory;

    public EngineFactory(
        IOptions<WorkingDirectorySettings> workingDirectorySettings,
        IOptions<EngineSettings> engineSettings,
        ILoggerFactory loggerFactory
    )
    {
        _workingDirectorySettings = workingDirectorySettings;
        _engineSettings = engineSettings;
        _loggerFactory = loggerFactory;
    }

    public IEngine Create(
        TaskContext context,
        string engine,
        JobMetadata metadata)
    {
        var moduleDirectoryService = new ModuleDirectoryService(
            metadata,
            _workingDirectorySettings
        );

        var additionalBinaryPaths = _engineSettings.Value.AdditionalBinaryPaths;

        return engine switch
        {
            "terraform" or "tofu" => new TerraformEngine(
                context,
                _loggerFactory.CreateLogger<TerraformEngine>(),
                moduleDirectoryService,
                engine,
                additionalBinaryPaths),
            "pulumi" => new PulumiEngine(
                context,
                _loggerFactory.CreateLogger<PulumiEngine>(),
                moduleDirectoryService,
                additionalBinaryPaths),
            _ => throw new NotSupportedException($"Engine '{engine}' is not supported")
        };
    }
}