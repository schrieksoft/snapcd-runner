using SnapCd.Common;
using SnapCd.Common.Dto;
using SnapCd.Common.Dto.Misc;

namespace SnapCd.Runner.Services.ModuleGetter;

public abstract class ModuleGetter
{
    protected readonly string TempWorkingFilesStashDir;
    protected readonly string SourceUrl;
    protected readonly string SourceRevision;
    protected readonly string Subdirectory;
    protected readonly SourceRevisionType SourceRevisionType;
    protected readonly TaskContext Context;
    protected readonly ILogger<ModuleGetter> Logger;
    protected readonly ModuleDirectoryService ModuleDirectoryService;
    protected string ResolvedSourceRevision = "";


    protected ModuleGetter(
        SourceRevisionType sourceRevisionType,
        string sourceUrl,
        string sourceRevision,
        string? subdirectory,
        ModuleDirectoryService moduleDirectoryService,
        TaskContext context,
        ILogger<ModuleGetter> logger
    )
    {
        SourceUrl = sourceUrl;
        SourceRevision = sourceRevision;
        Subdirectory = subdirectory ?? string.Empty;
        SourceRevisionType = sourceRevisionType;

        Context = context;
        Logger = logger;
        ModuleDirectoryService = moduleDirectoryService;

        TempWorkingFilesStashDir = Path.Combine(ModuleDirectoryService.GetTempDir(), "stash", "workingfiles");

        CreateDirectoryIfNotExists(ModuleDirectoryService.GetWorkingDir());
        CreateDirectoryIfNotExists(ModuleDirectoryService.GetTempDir());
        CreateDirectoryIfNotExists(TempWorkingFilesStashDir);
        CreateDirectoryIfNotExists(ModuleDirectoryService.GetModuleRootDir());
    }


    // abstract methods

    protected abstract Task<HashSet<string>> GetWorkingFiles();
    protected abstract Task<string> StashWorkingFiles(HashSet<string> workingFilePaths);
    protected abstract Task UnstashWorkingFiles(HashSet<string> workingFilePaths, string tempDir);
    protected abstract Task<string> GetLocalDefinitiveRevision();
    public abstract Task<string> GetRemoteDefinitiveRevision();
    public abstract Task<string> GetRemoteResolvedRevision();
    protected abstract Task DownloadModule(string resolvedRevision);

    protected abstract Task<bool> ConfirmModuleDownloaded();


    // concrete methods


    protected virtual Task Init()
    {
        return Task.CompletedTask;
    }


    protected virtual Task CreateSnapCdDir()
    {
        // Create the directory if it doesn't exist
        if (!Directory.Exists(ModuleDirectoryService.GetSnapCdDir()))
            Directory.CreateDirectory(ModuleDirectoryService.GetSnapCdDir());

        // Path to the .gitignore file
        var gitignorePath = Path.Combine(ModuleDirectoryService.GetSnapCdDir(), ".gitignore");

        // Write the .gitignore file to ignore everything, including itself
        File.WriteAllText(gitignorePath, "*\n");
        return Task.CompletedTask;
    }

    protected virtual async Task CleanupTemporaryFiles(string tempDir)
    {
        Context.LogInformation("Cleaning up temporary files");
        await DeleteDirectoryIfExists(tempDir);
    }

    public async Task GetModule(
        bool cleanInitEnabled,
        List<ExtraFileDto>? extraFiles,
        CancellationToken killCancellationToken = default,
        CancellationToken gracefulCancellationToken = default,
        string? remoteDefinitiveRevision = null)
    {
        await Init();
        var remoteResolvedRevision = await GetRemoteResolvedRevision();
        var localDefinitiveRevision = await GetLocalDefinitiveRevision();
        if (remoteDefinitiveRevision == null)
            remoteDefinitiveRevision = await GetRemoteDefinitiveRevision();

        var workingFilePaths = new HashSet<string>();
        var tempDir = string.Empty;

        if (!cleanInitEnabled)
        {
            workingFilePaths = await GetWorkingFiles();
            tempDir = await StashWorkingFiles(workingFilePaths);
        }

        try
        {
            if (localDefinitiveRevision != remoteDefinitiveRevision || localDefinitiveRevision == "")
            {
                Context.LogInformation(
                    !cleanInitEnabled
                        ? $"CleanInitEnabled set to 'false`. Stashing existing .terraform* files before redownloading module"
                        : $"Clean Init has been enabled. No existing files will be kept, module will be completely redownloaded.");

                await DeleteModuleRootDirIfExists();
                await DownloadModule(remoteResolvedRevision);
                if (!await ConfirmModuleDownloaded())
                    throw new Exception($"Module {ModuleDirectoryService.GetModuleRootDir()} not properly downloaded");
                if (!cleanInitEnabled)
                    await UnstashWorkingFiles(workingFilePaths, tempDir);
            }
            else
            {
                Context.LogInformation(
                    $"Local SHA equals latest remote SHA ({remoteDefinitiveRevision}), continuing with current local revision.");
            }

            await CreateSnapCdDir();
        }
        finally
        {
            await CleanupTemporaryFiles(tempDir);
        }

        await AddExtraFiles(workingFilePaths, extraFiles);
    }

    // concrete protected methods

    protected void CreateDirectoryIfNotExists(string directory)
    {
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
    }


    protected virtual async Task AddExtraFiles(HashSet<string> workingFilePaths, List<ExtraFileDto>? extraFiles)
    {
        if (extraFiles != null)
        {
            Context.LogInformation($"Now adding extra files to folder \"{ModuleDirectoryService.GetModuleRootDir()}\"");
            foreach (var file in extraFiles)
            {
                var path = Path.Combine(ModuleDirectoryService.GetModuleRootDir(), file.FileName);
                if (File.Exists(path) && !workingFilePaths.Contains(path))
                {
                    if (file.Overwrite)
                        await File.WriteAllTextAsync(path, file.Contents);
                }
                else
                {
                    await File.WriteAllTextAsync(path, file.Contents);
                }
            }
        }
    }

    protected virtual async Task DeleteModuleRootDirIfExists()
    {
        await DeleteDirectoryIfExists(ModuleDirectoryService.GetModuleRootDir());
    }

    protected virtual Task DeleteDirectoryIfExists(string directory)
    {
        try
        {
            // Check if the directory exists
            if (Directory.Exists(directory))
            {
                // Delete the directory and its contents
                Directory.Delete(directory, true);
                Context.LogInformation($"Directory {directory} deleted successfully");
            }
            else
            {
                Context.LogInformation($"Directory {directory} does not exist, doing nothing");
            }
        }
        catch (Exception ex)
        {
            // Handle any errors that might occur
            Context.LogError($"An unexpected error occured deleting project directory: \n {ex.Message}");
            throw new Exception($"An unexpected error occured deleting project directory: \n {ex.Message}");
        }

        return Task.CompletedTask;
    }
}