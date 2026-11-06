using DevOpsProjectMigrator.Components;
using DevOpsProjectMigrator.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

var builder = WebApplication.CreateBuilder(args);
var azureAdConfigured = Guid.TryParse(builder.Configuration["AzureAd:ClientId"], out _) &&
                        !string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:ClientSecret"]);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
if (azureAdConfigured)
{
    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddAuthorization();
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
        .EnableTokenAcquisitionToCallDownstreamApi([AzureDevOpsClient.Scope])
        .AddInMemoryTokenCaches();
    builder.Services.AddControllersWithViews()
        .AddMicrosoftIdentityUI();
    builder.Services.AddMicrosoftIdentityConsentHandler();
    builder.Services.AddHttpClient();
    builder.Services.AddScoped<AzureDevOpsClient>();
    builder.Services.AddScoped<GitRepositoryMigrator>();
    builder.Services.AddScoped<WikiMigrator>();
    builder.Services.AddScoped<WorkItemMigrator>();
    builder.Services.AddScoped<MigrationEngine>();
    builder.Services.AddServerSideBlazor()
        .AddMicrosoftIdentityConsentHandler();
    builder.Services.Configure<OpenIdConnectOptions>(
    OpenIdConnectDefaults.AuthenticationScheme,
    options => options.MapInboundClaims = false);
}

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

if (azureAdConfigured)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.UseAntiforgery();

if (azureAdConfigured)
{
    app.MapGet("/login", () => Results.Challenge(
            new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/" },
            [OpenIdConnectDefaults.AuthenticationScheme]))
        .AllowAnonymous();
    app.MapGet("/logout", () => Results.SignOut(
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));
    app.MapControllers();
}

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
