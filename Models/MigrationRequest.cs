using System.Text.Json.Serialization;

namespace DevOpsProjectMigrator.Models;

public enum DestinationMode
{
    CreateNew,
    UseExisting
}

public enum MigrationCategory
{
    GitRepositories,
    Wikis,
    Boards,
    Pipelines,
    Artifacts
}

public sealed class MigrationRequest
{
    public string SourceOrganization { get; set; } = string.Empty;
    public string SourceTenantId { get; set; } = string.Empty;
    public string SourceProjectId { get; set; } = string.Empty;
    public string DestinationOrganization { get; set; } = string.Empty;
    public string DestinationTenantId { get; set; } = string.Empty;
    public DestinationMode DestinationMode { get; set; } = DestinationMode.CreateNew;
    public string DestinationProjectId { get; set; } = string.Empty;
    public string NewProjectName { get; set; } = string.Empty;
    public string NewProjectDescription { get; set; } = string.Empty;
    public string ProcessTemplateId { get; set; } = string.Empty;
    public HashSet<MigrationCategory> Categories { get; set; } =
    [MigrationCategory.GitRepositories, MigrationCategory.Boards, MigrationCategory.Pipelines, MigrationCategory.Artifacts];
}

public sealed record ProjectProcessTemplate(
    string Name,
    [property: JsonPropertyName("typeId")] string Id,
    bool IsDefault);
