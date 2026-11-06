using System.Security.Claims;
using DevOpsProjectMigrator.Models;

namespace DevOpsProjectMigrator.Services;

public sealed class MigrationEngine(
    AzureDevOpsClient azureDevOps,
    GitRepositoryMigrator gitRepositoryMigrator,
    WikiMigrator wikiMigrator,
    WorkItemMigrator workItemMigrator)
{
    public async Task<MigrationReport> RunAsync(
        MigrationRequest request,
        ClaimsPrincipal user,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var report = new MigrationReport();
        var sourceProjects = await azureDevOps.ListProjectsAsync(
            request.SourceOrganization, request.SourceTenantId, user, cancellationToken);
        var sourceProject = sourceProjects.FirstOrDefault(project => project.Id == request.SourceProjectId)
            ?? throw new InvalidOperationException("The selected source project is no longer available.");
        report.SourceProjectName = sourceProject.Name;

        string destinationProjectId;
        if (request.DestinationMode == DestinationMode.CreateNew)
        {
            progress?.Invoke("Creating the destination project...");
            var destinationProject = await azureDevOps.CreateProjectAsync(
                request.DestinationOrganization,
                request.DestinationTenantId,
                user,
                request.NewProjectName,
                request.NewProjectDescription,
                request.ProcessTemplateId,
                cancellationToken);
            destinationProjectId = destinationProject.Id;
            report.DestinationProjectName = destinationProject.Name;
        }
        else
        {
            var destinationProjects = await azureDevOps.ListProjectsAsync(
                request.DestinationOrganization, request.DestinationTenantId, user, cancellationToken);
            var destinationProject = destinationProjects.FirstOrDefault(project => project.Id == request.DestinationProjectId)
                ?? throw new InvalidOperationException("The selected destination project is no longer available.");
            destinationProjectId = destinationProject.Id;
            report.DestinationProjectName = destinationProject.Name;
        }

        if (string.Equals(request.SourceOrganization, request.DestinationOrganization, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(request.SourceProjectId, destinationProjectId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The source and destination projects must be different.");
        }

        foreach (var category in request.Categories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke($"Processing {category}...");
            try
            {
                switch (category)
                {
                    case MigrationCategory.GitRepositories:
                        await gitRepositoryMigrator.MigrateAsync(request, destinationProjectId, user, report, cancellationToken);
                        if (!report.Items.Any(item => item.Category == "Git repositories"))
                        {
                            report.Items.Add(new MigrationItemResult("Git repositories", "Repository inventory", MigrationItemStatus.Completed, "The source project has no Git repositories."));
                        }
                        break;
                    case MigrationCategory.Wikis:
                        await wikiMigrator.MigrateAsync(request, destinationProjectId, user, report, cancellationToken);
                        break;
                    case MigrationCategory.Boards:
                        await workItemMigrator.MigrateAsync(request, destinationProjectId, user, report, progress, cancellationToken);
                        break;
                    case MigrationCategory.Pipelines:
                        report.Items.Add(new MigrationItemResult("Pipelines", "Pipeline definitions and runs", MigrationItemStatus.Skipped, "Pipeline definitions, service connections, secure variables, environments, permissions, and run history are not copied by this initial API migration."));
                        break;
                    case MigrationCategory.Artifacts:
                        report.Items.Add(new MigrationItemResult("Artifacts", "Feeds and package versions", MigrationItemStatus.Skipped, "Feed permissions and package contents require type-specific export/import. No package data was copied."));
                        break;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                report.Items.Add(new MigrationItemResult(category.ToString(), "Category operation", MigrationItemStatus.Failed, exception.Message));
            }
        }

        report.CompletedAt = DateTimeOffset.UtcNow;
        progress?.Invoke("Migration run finished.");
        return report;
    }

    private static void ValidateRequest(MigrationRequest request)
    {
        if (!Guid.TryParse(request.SourceProjectId, out _) ||
            !Guid.TryParse(request.SourceTenantId, out _) ||
            !Guid.TryParse(request.DestinationTenantId, out _))
        {
            throw new ArgumentException("Select a source project and enter valid Microsoft Entra tenant IDs.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceOrganization) ||
            string.IsNullOrWhiteSpace(request.DestinationOrganization) ||
            request.Categories.Count == 0)
        {
            throw new ArgumentException("Enter both Azure DevOps organizations and select at least one migration category.");
        }

        if (request.DestinationMode == DestinationMode.CreateNew &&
            (string.IsNullOrWhiteSpace(request.NewProjectName) || !Guid.TryParse(request.ProcessTemplateId, out _)))
        {
            throw new ArgumentException("Enter a name for the new destination project and select a process available in the destination organization.");
        }

        if (request.DestinationMode == DestinationMode.UseExisting && !Guid.TryParse(request.DestinationProjectId, out _))
        {
            throw new ArgumentException("Select an existing destination project.");
        }
    }
}
