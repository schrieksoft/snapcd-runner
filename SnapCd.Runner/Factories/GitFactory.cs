using SnapCd.Common;
using SnapCd.Runner.Services;
using SnapCd.Runner.Services.ModuleSourceRefresher;

namespace SnapCd.Runner.Factories;

public class GitFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public GitFactory(
        ILoggerFactory loggerFactory
    )
    {
        _loggerFactory = loggerFactory;
    }


    public Git Create(TaskContext context)
    {
        var logger = _loggerFactory.CreateLogger<Git>();

        return new Git(
            logger,
            context,
            new GitModuleSourceResolver()
        );
    }
}