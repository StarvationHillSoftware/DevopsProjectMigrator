using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevOpsProjectMigrator.Models;

namespace DevOpsProjectMigrator.Services;

public sealed class WorkItemMigrator(AzureDevOpsClient azureDevOps)
{
    private static readonly string[] CopyableFields =
    [
        "System.Title",
        "System.Description",
        "System.Tags",
        "Microsoft.VSTS.Common.Priority",
        "Microsoft.VSTS.Common.Severity",
        "Microsoft.VSTS.Common.AcceptanceCriteria",
        "Microsoft.VSTS.Scheduling.StoryPoints",
        "Microsoft.VSTS.Scheduling.Effort",
        "Microsoft.VSTS.Scheduling.RemainingWork",
        "Microsoft.VSTS.Scheduling.OriginalEstimate"
    ];

    public async Task MigrateAsync(
        MigrationRequest request,
        string destinationProjectId,
        ClaimsPrincipal user,
        MigrationReport report,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        using var query = await azureDevOps.SendJsonAsync(
            HttpMethod.Post,
            request.SourceOrganization,
            request.SourceTenantId,
            user,
            $"/{Uri.EscapeDataString(request.SourceProjectId)}/_apis/wit/wiql?api-version=7.1",
            new JsonObject { ["query"] = "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project ORDER BY [System.Id]" },
            "application/json",
            cancellationToken);

        if (!query.RootElement.TryGetProperty("workItems", out var workItems) || workItems.ValueKind != JsonValueKind.Array)
        {
            report.Items.Add(new MigrationItemResult("Boards", "Work items", MigrationItemStatus.Skipped, "The source WIQL query returned no work-item list."));
            return;
        }

        var ids = workItems.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetInt32() : 0)
            .Where(id => id > 0)
            .ToArray();
        var skippedRelations = 0;
        progress?.Invoke($"Copying {ids.Length} work item(s)...");

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var sourceItem = await azureDevOps.GetJsonAsync(
                    request.SourceOrganization,
                    request.SourceTenantId,
                    user,
                    $"/{Uri.EscapeDataString(request.SourceProjectId)}/_apis/wit/workitems/{id}?$expand=relations&api-version=7.1",
                    cancellationToken);

                var fields = sourceItem.RootElement.GetProperty("fields");
                var type = fields.GetProperty("System.WorkItemType").GetString()
                    ?? throw new InvalidOperationException("The source work item has no work item type.");
                var title = fields.TryGetProperty("System.Title", out var titleElement) ? titleElement : default;
                if (title.ValueKind == JsonValueKind.Undefined)
                {
                    throw new InvalidOperationException("The source work item has no title.");
                }

                var patch = new JsonArray();
                foreach (var field in CopyableFields)
                {
                    if (!fields.TryGetProperty(field, out var value) ||
                        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array)
                    {
                        continue;
                    }

                    patch.Add(new JsonObject
                    {
                        ["op"] = "add",
                        ["path"] = $"/fields/{field}",
                        ["value"] = JsonNode.Parse(value.GetRawText())
                    });
                }

                using var created = await azureDevOps.SendJsonAsync(
                    HttpMethod.Post,
                    request.DestinationOrganization,
                    request.DestinationTenantId,
                    user,
                    $"/{Uri.EscapeDataString(destinationProjectId)}/_apis/wit/workitems/${Uri.EscapeDataString(type)}?api-version=7.1",
                    patch,
                    "application/json-patch+json",
                    cancellationToken);

                if (sourceItem.RootElement.TryGetProperty("relations", out var relations) && relations.ValueKind == JsonValueKind.Array)
                {
                    skippedRelations += relations.GetArrayLength();
                }

                report.Items.Add(new MigrationItemResult("Boards", $"Work item {id}", MigrationItemStatus.Completed, $"Created as work item {created.RootElement.GetProperty("id").GetInt32()}; copied title and supported common fields."));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                report.Items.Add(new MigrationItemResult("Boards", $"Work item {id}", MigrationItemStatus.Failed, exception.Message));
            }
        }

        if (skippedRelations > 0)
        {
            report.Items.Add(new MigrationItemResult("Boards", "Work item relations and attachments", MigrationItemStatus.Skipped, $"{skippedRelations} relation(s) were not recreated; attachment links, hierarchy, hyperlinks, and related work-item links require ID mapping."));
        }

        if (ids.Length >= 20_000)
        {
            report.Items.Add(new MigrationItemResult("Boards", "Work item query limit", MigrationItemStatus.Skipped, "The WIQL result reached 20,000 items; verify whether the source project has more work items."));
        }

        report.Items.Add(new MigrationItemResult("Boards", "History, identity, and process data", MigrationItemStatus.Skipped, "Revision history, original IDs/dates, identities, comments, states, area/iteration paths, dashboards, and process customization are not preserved by this API replay."));
    }
}
