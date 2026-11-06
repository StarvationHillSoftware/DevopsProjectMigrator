using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevOpsProjectMigrator.Models;

namespace DevOpsProjectMigrator.Services;

public sealed class WikiMigrator(AzureDevOpsClient azureDevOps)
{
    private const string WikiApiVersion = "7.1-preview.2";

    public async Task MigrateAsync(
        MigrationRequest request,
        string destinationProjectId,
        ClaimsPrincipal user,
        MigrationReport report,
        CancellationToken cancellationToken)
    {
        using var sourceWikisResponse = await azureDevOps.GetJsonAsync(
            request.SourceOrganization,
            request.SourceTenantId,
            user,
            $"/{Uri.EscapeDataString(request.SourceProjectId)}/_apis/wiki/wikis?api-version={WikiApiVersion}",
            cancellationToken);
        using var targetWikisResponse = await azureDevOps.GetJsonAsync(
            request.DestinationOrganization,
            request.DestinationTenantId,
            user,
            $"/{Uri.EscapeDataString(destinationProjectId)}/_apis/wiki/wikis?api-version={WikiApiVersion}",
            cancellationToken);
        using var sourceRepositoriesResponse = await azureDevOps.GetJsonAsync(
            request.SourceOrganization,
            request.SourceTenantId,
            user,
            $"/{Uri.EscapeDataString(request.SourceProjectId)}/_apis/git/repositories?includeHidden=true&api-version=7.1-preview.1",
            cancellationToken);
        using var targetRepositoriesResponse = await azureDevOps.GetJsonAsync(
            request.DestinationOrganization,
            request.DestinationTenantId,
            user,
            $"/{Uri.EscapeDataString(destinationProjectId)}/_apis/git/repositories?includeHidden=true&api-version=7.1-preview.1",
            cancellationToken);

        var sourceWikis = ReadArray(sourceWikisResponse.RootElement);
        var targetWikis = ReadArray(targetWikisResponse.RootElement);
        var sourceRepositories = ReadArray(sourceRepositoriesResponse.RootElement);
        var targetRepositories = ReadArray(targetRepositoriesResponse.RootElement);
        var sourceToken = await azureDevOps.GetAccessTokenAsync(request.SourceTenantId, user);
        var destinationToken = await azureDevOps.GetAccessTokenAsync(request.DestinationTenantId, user);
        var gitLfsAvailable = await GitRepositoryMigrator.IsGitLfsAvailableAsync(cancellationToken);

        if (sourceWikis.Count == 0)
        {
            report.Items.Add(new MigrationItemResult("Wikis", "Wiki inventory", MigrationItemStatus.Completed, "The source project has no accessible wikis."));
            return;
        }

        foreach (var sourceWiki in sourceWikis)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = GetString(sourceWiki, "name") ?? "Unnamed wiki";
            try
            {
                var type = GetString(sourceWiki, "type") ?? "projectWiki";
                var isProjectWiki = string.Equals(type, "projectWiki", StringComparison.OrdinalIgnoreCase);
                var existing = targetWikis.Where(wiki =>
                    string.Equals(GetString(wiki, "name"), name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetString(wiki, "type"), type, StringComparison.OrdinalIgnoreCase)).Any();
                if (existing)
                {
                    report.Items.Add(new MigrationItemResult("Wikis", name, MigrationItemStatus.Skipped, "A wiki with this name and type already exists in the destination; it was not overwritten."));
                    continue;
                }

                var sourceRepositoryId = GetString(sourceWiki, "repositoryId");
                if (string.IsNullOrWhiteSpace(sourceRepositoryId))
                {
                    throw new InvalidOperationException("Azure DevOps did not return the Git repository backing this wiki.");
                }

                var sourceRepository = sourceRepositories.Where(repository =>
                    string.Equals(GetString(repository, "id"), sourceRepositoryId, StringComparison.OrdinalIgnoreCase))
                    .Select(repository => (JsonElement?)repository)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException("The Git repository backing this wiki was not returned by the repository API. Check access and hidden-repository visibility.");
                var sourceRepositoryValue = sourceRepository;
                var sourceRepositoryName = GetString(sourceRepositoryValue, "name")
                    ?? throw new InvalidOperationException("Azure DevOps returned a wiki repository without a name.");
                JsonElement? targetRepository = targetRepositories.Where(repository =>
                    string.Equals(GetString(repository, "name"), sourceRepositoryName, StringComparison.OrdinalIgnoreCase))
                    .Select(repository => (JsonElement?)repository)
                    .FirstOrDefault();

                if (isProjectWiki)
                {
                    var existingProjectWiki = targetWikis.Any(wiki =>
                        string.Equals(GetString(wiki, "type"), "projectWiki", StringComparison.OrdinalIgnoreCase));
                    if (existingProjectWiki)
                    {
                        report.Items.Add(new MigrationItemResult("Wikis", name, MigrationItemStatus.Skipped, "The destination already has its project wiki; it was not overwritten."));
                        continue;
                    }

                    using var createdWiki = await azureDevOps.SendJsonAsync(
                        HttpMethod.Post,
                        request.DestinationOrganization,
                        request.DestinationTenantId,
                        user,
                        $"/{Uri.EscapeDataString(destinationProjectId)}/_apis/wiki/wikis?api-version={WikiApiVersion}",
                        new JsonObject
                        {
                            ["type"] = "projectWiki",
                            ["projectId"] = destinationProjectId
                        },
                        "application/json",
                        cancellationToken);
                    var targetRepositoryId = GetString(createdWiki.RootElement, "repositoryId")
                        ?? throw new InvalidOperationException("Azure DevOps created the project wiki but did not return its backing repository ID.");
                    targetRepository = targetRepositories.Where(repository =>
                        string.Equals(GetString(repository, "id"), targetRepositoryId, StringComparison.OrdinalIgnoreCase))
                        .Select(repository => (JsonElement?)repository)
                        .FirstOrDefault();
                    if (targetRepository is null)
                    {
                        using var refreshedRepositories = await azureDevOps.GetJsonAsync(
                            request.DestinationOrganization,
                            request.DestinationTenantId,
                            user,
                            $"/{Uri.EscapeDataString(destinationProjectId)}/_apis/git/repositories?includeHidden=true&api-version=7.1-preview.1",
                            cancellationToken);
                        targetRepository = ReadArray(refreshedRepositories.RootElement).Where(repository =>
                            string.Equals(GetString(repository, "id"), targetRepositoryId, StringComparison.OrdinalIgnoreCase))
                            .Select(repository => (JsonElement?)repository)
                            .FirstOrDefault();
                    }
                    if (targetRepository is null)
                    {
                        throw new InvalidOperationException("The project wiki was created, but its backing Git repository could not be found for content transfer.");
                    }
                }
                else
                {
                    var repositoryAlreadyMigrated = request.Categories.Contains(MigrationCategory.GitRepositories) &&
                        report.Items.Any(item =>
                            item.Category == "Git repositories" &&
                            item.Item == sourceRepositoryName &&
                            item.Status == MigrationItemStatus.Completed);
                    if (targetRepository is null)
                    {
                        using var createdRepository = await azureDevOps.SendJsonAsync(
                            HttpMethod.Post,
                            request.DestinationOrganization,
                            request.DestinationTenantId,
                            user,
                            $"/{Uri.EscapeDataString(destinationProjectId)}/_apis/git/repositories?api-version=7.1",
                            new JsonObject
                            {
                                ["name"] = sourceRepositoryName,
                                ["project"] = new JsonObject { ["id"] = destinationProjectId }
                            },
                            "application/json",
                            cancellationToken);
                        targetRepository = createdRepository.RootElement.Clone();
                    }
                    var targetRepositoryValue = targetRepository!.Value;

                    if (!repositoryAlreadyMigrated && await HasRefsAsync(request, destinationProjectId, targetRepositoryValue, user, cancellationToken))
                    {
                        report.Items.Add(new MigrationItemResult("Wikis", name, MigrationItemStatus.Skipped, "The destination backing repository already contains refs and was not overwritten. Publish/register this repo as a wiki manually if appropriate."));
                        continue;
                    }

                    if (!repositoryAlreadyMigrated)
                    {
                        await MirrorWikiRepositoryAsync(
                            request,
                            destinationProjectId,
                            sourceRepositoryValue,
                            targetRepositoryValue,
                            sourceToken,
                            destinationToken,
                            gitLfsAvailable,
                            cancellationToken);
                    }

                    var branch = GetString(sourceRepositoryValue, "defaultBranch")?.Replace("refs/heads/", "", StringComparison.OrdinalIgnoreCase)
                        ?? GetFirstWikiVersion(sourceWiki)
                        ?? throw new InvalidOperationException("The code wiki has no source branch version to publish.");
                    var createCodeWikiPayload = new JsonObject
                    {
                        ["type"] = "codeWiki",
                        ["name"] = name,
                        ["projectId"] = destinationProjectId,
                        ["repositoryId"] = GetString(targetRepositoryValue, "id"),
                        ["mappedPath"] = GetString(sourceWiki, "mappedPath") ?? "/",
                        ["version"] = new JsonObject
                        {
                            ["version"] = branch,
                            ["versionType"] = "branch"
                        }
                    };
                    using var createdCodeWiki = await azureDevOps.SendJsonAsync(
                        HttpMethod.Post,
                        request.DestinationOrganization,
                        request.DestinationTenantId,
                        user,
                        $"/{Uri.EscapeDataString(destinationProjectId)}/_apis/wiki/wikis?api-version={WikiApiVersion}",
                        createCodeWikiPayload,
                        "application/json",
                        cancellationToken);
                }

                if (isProjectWiki)
                {
                    await MirrorWikiRepositoryAsync(
                        request,
                        destinationProjectId,
                        sourceRepositoryValue,
                        targetRepository!.Value,
                        sourceToken,
                        destinationToken,
                        gitLfsAvailable,
                        cancellationToken);
                }

                report.Items.Add(new MigrationItemResult("Wikis", name, MigrationItemStatus.Completed, "Wiki registration and Git content/history were migrated."));
                if (!gitLfsAvailable)
                {
                    report.Items.Add(new MigrationItemResult("Git LFS", name, MigrationItemStatus.Skipped, "Git wiki content was mirrored, but Git LFS objects were not transferred because Git LFS is not installed on the migration host."));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                report.Items.Add(new MigrationItemResult("Wikis", name, MigrationItemStatus.Failed, exception.Message));
            }
        }
    }

    private async Task<bool> HasRefsAsync(
        MigrationRequest request,
        string destinationProjectId,
        JsonElement repository,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var repositoryId = GetString(repository, "id")
            ?? throw new InvalidOperationException("Azure DevOps returned a destination wiki repository without an ID.");
        using var refs = await azureDevOps.GetJsonAsync(
            request.DestinationOrganization,
            request.DestinationTenantId,
            user,
            $"/{Uri.EscapeDataString(destinationProjectId)}/_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/refs?api-version=7.1",
            cancellationToken);
        return refs.RootElement.TryGetProperty("value", out var values) &&
               values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0;
    }

    private static async Task MirrorWikiRepositoryAsync(
        MigrationRequest request,
        string destinationProjectId,
        JsonElement sourceRepository,
        JsonElement targetRepository,
        string sourceToken,
        string destinationToken,
        bool gitLfsAvailable,
        CancellationToken cancellationToken)
    {
        var sourceRepositoryId = GetString(sourceRepository, "id")
            ?? throw new InvalidOperationException("Azure DevOps returned a wiki repository without an ID.");
        var targetRepositoryId = GetString(targetRepository, "id")
            ?? throw new InvalidOperationException("Azure DevOps returned a destination wiki repository without an ID.");
        await GitRepositoryMigrator.MirrorRepositoryAsync(
            request.SourceOrganization,
            request.SourceProjectId,
            sourceRepositoryId,
            request.DestinationOrganization,
            destinationProjectId,
            targetRepositoryId,
            GetString(sourceRepository, "remoteUrl"),
            GetString(targetRepository, "remoteUrl"),
            sourceToken,
            destinationToken,
            gitLfsAvailable,
            cancellationToken);
    }

    private static IReadOnlyList<JsonElement> ReadArray(JsonElement root) =>
        root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.Clone()).ToArray()
            : [];

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? GetFirstWikiVersion(JsonElement wiki)
    {
        if (!wiki.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var version in versions.EnumerateArray())
        {
            var name = GetString(version, "version");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name.Replace("refs/heads/", "", StringComparison.OrdinalIgnoreCase);
            }
        }

        return null;
    }
}
