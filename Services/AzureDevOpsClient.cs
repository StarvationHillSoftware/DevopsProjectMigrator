using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Identity.Web;
using System.Security.Claims;
using DevOpsProjectMigrator.Models;

namespace DevOpsProjectMigrator.Services;

public sealed class AzureDevOpsClient(
    IHttpClientFactory httpClientFactory,
    ITokenAcquisition tokenAcquisition)
{
    public const string Scope = "499b84ac-1321-427f-aa17-267ca6975798/.default";
    private const string ApiVersion = "7.1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AzureDevOpsProject>> ListProjectsAsync(
        string organization,
        string tenantId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ValidateOrganization(organization);
        var token = await GetAccessTokenAsync(tenantId, user);
        var uri = new Uri($"https://dev.azure.com/{Uri.EscapeDataString(organization)}/_apis/projects?stateFilter=wellFormed&$top=1000&api-version={ApiVersion}");
        using var response = await SendWithRetryAsync(uri, token, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ProjectsResponse>(JsonOptions, cancellationToken);
        return result?.Value ?? [];
    }

    public async Task<IReadOnlyList<ProjectProcessTemplate>> ListProcessesAsync(
        string organization,
        string tenantId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ValidateOrganization(organization);
        var token = await GetAccessTokenAsync(tenantId, user);
        var uri = new Uri($"https://dev.azure.com/{Uri.EscapeDataString(organization)}/_apis/work/processes?api-version=7.1-preview.2");
        using var response = await SendWithRetryAsync(uri, token, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ProcessesResponse>(JsonOptions, cancellationToken);
        return result?.Value ?? [];
    }

    public async Task<AzureDevOpsProject> CreateProjectAsync(
        string organization,
        string tenantId,
        ClaimsPrincipal user,
        string name,
        string? description,
        string processTemplateId,
        CancellationToken cancellationToken = default)
    {
        ValidateOrganization(organization);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
        {
            throw new ArgumentException("Project names must contain between 1 and 64 characters.", nameof(name));
        }

        if (!Guid.TryParse(processTemplateId, out _))
        {
            throw new ArgumentException("Select a valid Azure DevOps process template.", nameof(processTemplateId));
        }

        var token = await GetAccessTokenAsync(tenantId, user);
        var uri = new Uri($"https://dev.azure.com/{Uri.EscapeDataString(organization)}/_apis/projects?api-version={ApiVersion}");
        var payload = new
        {
            name,
            description,
            capabilities = new
            {
                versioncontrol = new { sourceControlType = "Git" },
                processTemplate = new { templateTypeId = processTemplateId }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var serviceMessage = await ReadServiceErrorAsync(response, cancellationToken);
            throw new HttpRequestException($"Azure DevOps could not create the project (HTTP {(int)response.StatusCode}): {serviceMessage}");
        }

        using var operation = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!operation.RootElement.TryGetProperty("id", out var operationIdElement) ||
            !Guid.TryParse(operationIdElement.GetString(), out var operationId))
        {
            throw new InvalidOperationException("Azure DevOps accepted the project request but did not return an operation ID.");
        }

        var operationUri = new Uri($"https://dev.azure.com/{Uri.EscapeDataString(organization)}/_apis/operations/{operationId:D}?api-version=7.1-preview.1");
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            using var operationResponse = await SendWithRetryAsync(operationUri, token, cancellationToken);
            using var operationStatus = await JsonDocument.ParseAsync(
                await operationResponse.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var status = operationStatus.RootElement.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;

            if (string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                var projects = await ListProjectsAsync(organization, tenantId, user, cancellationToken);
                return projects.FirstOrDefault(project => string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The project was created, but Azure DevOps did not return it in the project list.");
            }

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Azure DevOps project creation ended with status '{status}'.");
            }
        }

        throw new TimeoutException("Azure DevOps project creation did not finish within two minutes.");
    }

    internal async Task<JsonDocument> GetJsonAsync(
        string organization,
        string tenantId,
        ClaimsPrincipal user,
        string apiPath,
        CancellationToken cancellationToken)
    {
        var uri = BuildApiUri(organization, apiPath);
        var token = await GetAccessTokenAsync(tenantId, user);
        using var response = await SendWithRetryAsync(uri, token, cancellationToken);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    internal async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string organization,
        string tenantId,
        ClaimsPrincipal user,
        string apiPath,
        JsonNode payload,
        string contentType,
        CancellationToken cancellationToken)
    {
        var uri = BuildApiUri(organization, apiPath);
        var token = await GetAccessTokenAsync(tenantId, user);
        using var request = new HttpRequestMessage(method, uri)
        {
            Content = JsonContent.Create(payload)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Azure DevOps returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    internal async Task<string> GetAccessTokenAsync(string tenantId, ClaimsPrincipal user)
    {
        if (!Guid.TryParse(tenantId, out _))
        {
            throw new ArgumentException("Enter the Microsoft Entra tenant ID as a GUID.", nameof(tenantId));
        }

        return await tokenAcquisition.GetAccessTokenForUserAsync(
            [Scope],
            tenantId: tenantId,
            user: user);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Uri uri,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var response = await client.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests ||
                            (int)response.StatusCode >= 500;
            if (!retryable || attempt >= 4)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"Azure DevOps returned HTTP {(int)status} ({status}).");
            }

            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            if (delay < TimeSpan.Zero || delay > TimeSpan.FromSeconds(60))
            {
                delay = TimeSpan.FromSeconds(60);
            }
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static Uri BuildApiUri(string organization, string apiPath)
    {
        ValidateOrganization(organization);
        if (!apiPath.StartsWith('/') || apiPath.StartsWith("//", StringComparison.Ordinal) || apiPath.Contains('#'))
        {
            throw new ArgumentException("Azure DevOps API paths must be relative to the organization.", nameof(apiPath));
        }

        return new Uri($"https://dev.azure.com/{Uri.EscapeDataString(organization)}{apiPath}", UriKind.Absolute);
    }

    private static async Task<string> ReadServiceErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var error = JsonDocument.Parse(body);
            if (error.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? "No error details were returned.";
            }
        }
        catch (JsonException)
        {
        }

        return body.Length <= 1000 ? body : body[..1000];
    }

    private static void ValidateOrganization(string organization)
    {
        if (string.IsNullOrWhiteSpace(organization) ||
            organization.Length > 64 ||
            !organization.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
        {
            throw new ArgumentException("Enter a valid Azure DevOps organization name.", nameof(organization));
        }
    }

    private sealed record ProjectsResponse(IReadOnlyList<AzureDevOpsProject>? Value);
    private sealed record ProcessesResponse(IReadOnlyList<ProjectProcessTemplate>? Value);
}

public sealed record AzureDevOpsProject(
    string Id,
    string Name,
    string? Description,
    string? State,
    string? Url);
