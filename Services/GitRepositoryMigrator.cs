using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using DevOpsProjectMigrator.Models;

namespace DevOpsProjectMigrator.Services;

public sealed class GitRepositoryMigrator(AzureDevOpsClient azureDevOps)
{
    public async Task MigrateAsync(
        MigrationRequest request,
        string destinationProjectId,
        ClaimsPrincipal user,
        MigrationReport report,
        CancellationToken cancellationToken)
    {
        using var sourceResponse = await azureDevOps.GetJsonAsync(
            request.SourceOrganization,
            request.SourceTenantId,
            user,
            $"/{Uri.EscapeDataString(request.SourceProjectId)}/_apis/git/repositories?api-version=7.1",
            cancellationToken);
        using var targetResponse = await azureDevOps.GetJsonAsync(
            request.DestinationOrganization,
            request.DestinationTenantId,
            user,
            $"/{Uri.EscapeDataString(destinationProjectId)}/_apis/git/repositories?api-version=7.1",
            cancellationToken);

        var sourceRepositories = ReadRepositories(sourceResponse.RootElement);
        var targetRepositories = ReadRepositories(targetResponse.RootElement);
        var sourceToken = await azureDevOps.GetAccessTokenAsync(request.SourceTenantId, user);
        var targetToken = await azureDevOps.GetAccessTokenAsync(request.DestinationTenantId, user);
        var gitLfsAvailable = await IsGitLfsAvailableAsync(cancellationToken);

        foreach (var sourceRepository in sourceRepositories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetRepository = targetRepositories.FirstOrDefault(repository =>
                string.Equals(repository.Name, sourceRepository.Name, StringComparison.OrdinalIgnoreCase));
            var created = false;
            try
            {
                if (targetRepository is null)
                {
                    using var createResponse = await azureDevOps.SendJsonAsync(
                        HttpMethod.Post,
                        request.DestinationOrganization,
                        request.DestinationTenantId,
                        user,
                        $"/{Uri.EscapeDataString(destinationProjectId)}/_apis/git/repositories?api-version=7.1",
                        System.Text.Json.Nodes.JsonNode.Parse($"{{\"name\":{JsonSerializer.Serialize(sourceRepository.Name)},\"project\":{{\"id\":{JsonSerializer.Serialize(destinationProjectId)}}}}}")!,
                        "application/json",
                        cancellationToken);
                    targetRepository = ReadRepository(createResponse.RootElement);
                    created = true;
                }
                else
                {
                    using var refs = await azureDevOps.GetJsonAsync(
                        request.DestinationOrganization,
                        request.DestinationTenantId,
                        user,
                        $"/{Uri.EscapeDataString(destinationProjectId)}/_apis/git/repositories/{Uri.EscapeDataString(targetRepository.Id)}/refs?api-version=7.1",
                        cancellationToken);
                    if (refs.RootElement.TryGetProperty("value", out var refValues) && refValues.GetArrayLength() > 0)
                    {
                        report.Items.Add(new MigrationItemResult("Git repositories", sourceRepository.Name, MigrationItemStatus.Skipped, "A destination repository with this name already contains refs; it was not overwritten."));
                        continue;
                    }
                }

                await MirrorRepositoryAsync(
                    request.SourceOrganization,
                    request.SourceProjectId,
                    sourceRepository.Id,
                    request.DestinationOrganization,
                    destinationProjectId,
                    targetRepository.Id,
                    sourceRepository.RemoteUrl,
                    targetRepository.RemoteUrl,
                    sourceToken,
                    targetToken,
                    gitLfsAvailable,
                    cancellationToken);
                report.Items.Add(new MigrationItemResult("Git repositories", sourceRepository.Name, MigrationItemStatus.Completed, created ? "Repository created and Git refs mirrored." : "Git refs mirrored into the empty destination repository."));
                if (gitLfsAvailable)
                {
                    report.Items.Add(new MigrationItemResult("Git LFS", sourceRepository.Name, MigrationItemStatus.Completed, "Git LFS objects were fetched and pushed."));
                }
                if (!gitLfsAvailable)
                {
                    report.Items.Add(new MigrationItemResult("Git LFS", sourceRepository.Name, MigrationItemStatus.Skipped, "Git refs were mirrored, but Git LFS objects were not transferred because Git LFS is not installed on the migration host."));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                report.Items.Add(new MigrationItemResult("Git repositories", sourceRepository.Name, MigrationItemStatus.Failed, exception.Message));
            }
        }
    }

    private static IReadOnlyList<GitRepository> ReadRepositories(JsonElement root)
    {
        if (!root.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray().Select(ReadRepository).ToArray();
    }

    private static GitRepository ReadRepository(JsonElement item) => new(
        item.GetProperty("id").GetString() ?? throw new InvalidOperationException("Azure DevOps returned a repository without an ID."),
        item.GetProperty("name").GetString() ?? throw new InvalidOperationException("Azure DevOps returned a repository without a name."),
        item.TryGetProperty("remoteUrl", out var remoteUrl) ? remoteUrl.GetString() : null);

    internal static async Task MirrorRepositoryAsync(
        string sourceOrganization,
        string sourceProjectId,
        string sourceRepositoryId,
        string destinationOrganization,
        string destinationProjectId,
        string destinationRepositoryId,
        string? sourceRemoteUrl,
        string? destinationRemoteUrl,
        string sourceToken,
        string destinationToken,
        bool gitLfsAvailable,
        CancellationToken cancellationToken)
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "DevOpsProjectMigrator", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var repositoryPath = Path.Combine(workingDirectory, "repository.git");
            var sourceUrl = sourceRemoteUrl ?? $"https://dev.azure.com/{Uri.EscapeDataString(sourceOrganization)}/{Uri.EscapeDataString(sourceProjectId)}/_git/{Uri.EscapeDataString(sourceRepositoryId)}";
            var destinationUrl = destinationRemoteUrl ?? $"https://dev.azure.com/{Uri.EscapeDataString(destinationOrganization)}/{Uri.EscapeDataString(destinationProjectId)}/_git/{Uri.EscapeDataString(destinationRepositoryId)}";
            await RunGitAsync(workingDirectory, ["clone", "--mirror", "--quiet", sourceUrl, repositoryPath], sourceOrganization, sourceToken, cancellationToken);
            if (gitLfsAvailable)
            {
                await RunGitAsync(repositoryPath, ["lfs", "fetch", "--all", "origin"], sourceOrganization, sourceToken, cancellationToken);
            }
            await RunGitAsync(repositoryPath, ["push", "--mirror", "--quiet", destinationUrl], destinationOrganization, destinationToken, cancellationToken);
            if (gitLfsAvailable)
            {
                await RunGitAsync(repositoryPath, ["lfs", "push", "--all", destinationUrl], destinationOrganization, destinationToken, cancellationToken);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static async Task<bool> IsGitLfsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("lfs");
            startInfo.ArgumentList.Add("version");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string organization,
        string token,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_CONFIG_COUNT"] = "1";
        startInfo.Environment["GIT_CONFIG_KEY_0"] = $"http.https://dev.azure.com/{organization}/.extraheader";
        startInfo.Environment["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: bearer {token}";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git could not be started. Install Git and ensure it is available on PATH.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException($"Git exited with code {process.ExitCode}: {details.Trim()}");
        }
    }

    private sealed record GitRepository(string Id, string Name, string? RemoteUrl);
}
