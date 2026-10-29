using Ollamazing.Web.Catalog;
using Ollamazing.Web.Components;
using Ollamazing.Web.Configuration;
using Ollamazing.Web.Ollama;
using Ollamazing.Web.Server;
using Microsoft.Extensions.Options;
using OllamaSharp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));
builder.Services.Configure<ServerControlOptions>(builder.Configuration.GetSection(ServerControlOptions.SectionName));
builder.Services.Configure<CatalogOptions>(builder.Configuration.GetSection(CatalogOptions.SectionName));

// A single OllamaSharp client serves every circuit; the infinite timeout lets multi-gigabyte pulls stream to the end.
builder.Services.AddSingleton<IOllamaApiClient>(services =>
{
    OllamaOptions ollamaOptions = services.GetRequiredService<IOptions<OllamaOptions>>().Value;
    HttpClient ollamaHttpClient = new() { BaseAddress = new Uri(ollamaOptions.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };
    return new OllamaApiClient(ollamaHttpClient);
});

// The catalog scraper identifies itself to ollama.com and gives up on pages that hang.
builder.Services.AddHttpClient<OllamaCatalogScraper>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Ollamazing/1.0 (+https://github.com/mdesalvo/ollamazing)");
});

// Shared state lives in singletons, so downloads, catalog and status are the same for every open tab.
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddSingleton<PullManager>();
builder.Services.AddSingleton<OllamaServerController>();
builder.Services.AddSingleton<ServerStatusMonitor>();
builder.Services.AddSingleton<AccountMonitor>();

// Auto-start runs before the first status probe, so the dashboard opens on a server that is already coming up.
builder.Services.AddHostedService<ServerAutoStartService>();
builder.Services.AddHostedService(services => services.GetRequiredService<ServerStatusMonitor>());
builder.Services.AddHostedService<CatalogStartupService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
