using Microsoft.FluentUI.AspNetCore.Components;
using Ollamazing.Server.Extensions;
using Ollamazing.Web.Catalog;
using Ollamazing.Web.Components;

// The builder collects the services and the configuration of the dashboard.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Pages are Razor components that run on the server and talk to the browser over a live circuit.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Fluent UI draws every control of the dashboard; on Blazor Server it needs a default HttpClient registered before it.
builder.Services.AddHttpClient();
builder.Services.AddFluentUIComponents();

// Server control, status, account link and downloads come from the server library, shared by every open tab.
builder.Services.AddOllamazingServer(builder.Configuration);

// The Catalog section of the configuration feeds the scraper and the store.
builder.Services.Configure<CatalogOptions>(builder.Configuration.GetSection(CatalogOptions.SectionName));

// The catalog scraper identifies itself to ollama.com and gives up on pages that hang.
builder.Services.AddHttpClient<OllamaCatalogScraper>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Ollamazing/1.0 (+https://github.com/mdesalvo/ollamazing)");
});

// The catalog is shared by every open tab and loads after the server services have started.
builder.Services.AddSingleton<CatalogStore>();
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddHostedService<CatalogStartupService>();

// The application is built only now, as no service can be added after this point.
WebApplication app = builder.Build();

// Outside development, unhandled errors show the friendly error page instead of a stack trace.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
// Unknown addresses render the not-found page inside the app layout.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Interactive forms are protected against cross-site requests.
app.UseAntiforgery();

// Scripts and styles of the dashboard are served with fingerprinted URLs.
app.MapStaticAssets();

// The root component serves every page, interactive on the server.
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

// The dashboard serves until the host is stopped.
await app.RunAsync();