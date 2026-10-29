using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaSharp;
using Ollamazing.Server.Account;
using Ollamazing.Server.Configuration;
using Ollamazing.Server.Lifecycle;
using Ollamazing.Server.Pulls;
using Ollamazing.Server.Status;

namespace Ollamazing.Server.Extensions;

/// <summary>Registers the services that manage the local Ollama server: process, status, account link and downloads.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Adds every server service as a shared singleton and starts auto-start and status probing with the host.</summary>
    /// <param name="configuration">Root configuration holding the <c>Ollama</c> and <c>ServerControl</c> sections.</param>
    public static IServiceCollection AddOllamazingServer(this IServiceCollection services, IConfiguration configuration)
    {
        // Both sections are read from the configuration of the host, so one appsettings file serves every project.
        services.Configure<OllamaOptions>(configuration.GetSection(OllamaOptions.SectionName));
        services.Configure<ServerControlOptions>(configuration.GetSection(ServerControlOptions.SectionName));

        // A single OllamaSharp client serves every consumer; the infinite timeout lets multi-gigabyte pulls stream to the end.
        services.AddSingleton<IOllamaApiClient>(serviceProvider =>
        {
            OllamaOptions ollamaOptions = serviceProvider.GetRequiredService<IOptions<OllamaOptions>>().Value;
            HttpClient ollamaHttpClient = new() { BaseAddress = new Uri(ollamaOptions.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };
            return new OllamaApiClient(ollamaHttpClient);
        });

        // Shared state lives in singletons, so downloads, status and account are the same for every consumer.
        services.AddSingleton<OllamaServerController>();
        services.AddSingleton<ServerStatusMonitor>();
        services.AddSingleton<AccountMonitor>();
        services.AddSingleton<PullManager>();

        // Auto-start runs before the first status probe, so consumers see a server that is already coming up.
        services.AddHostedService<ServerAutoStartService>();
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ServerStatusMonitor>());
        return services;
    }
}
