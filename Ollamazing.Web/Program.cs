using Microsoft.FluentUI.AspNetCore.Components;
using Ollamazing.Server.Extensions;
using Ollamazing.Web.Catalog;
using Ollamazing.Web.Components;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Fluent UI draws every control of the dashboard; on Blazor Server it needs a default HttpClient registered before it.
builder.Services.AddHttpClient();
builder.Services.AddFluentUIComponents();

// Server control, status, account link and downloads come from the server library, shared by every open tab.
builder.Services.AddOllamazingServer(builder.Configuration);

builder.Services.Configure<CatalogOptions>(builder.Configuration.GetSection(CatalogOptions.SectionName));

// The catalog scraper identifies itself to ollama.com and gives up on pages that hang.
builder.Services.AddHttpClient<OllamaCatalogScraper>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Ollamazing/1.0 (+https://github.com/mdesalvo/ollamazing)");
});

// The catalog is shared by every open tab and loads after the server services have started.
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddHostedService<CatalogStartupService>();

WebApplication app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

await app.RunAsync();