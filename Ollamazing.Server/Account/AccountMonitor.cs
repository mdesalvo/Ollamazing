using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using Ollamazing.Server.Configuration;
using Ollamazing.Server.Extensions;
using Ollamazing.Server.Status;

namespace Ollamazing.Server.Account;

/// <summary>Publishes to every circuit whether the local server is linked to an ollama.com account, and to which one.</summary>
/// <remarks>
/// Singleton: the account is asked to the server whenever the server comes up and on demand, such as while pairing.
/// The link belongs to the server key, so every tab and the sidebar share the same answer.
/// </remarks>
public sealed class AccountMonitor : IDisposable
{
    private readonly IOllamaApiClient ollamaClient;
    private readonly ServerStatusMonitor statusMonitor;
    private readonly ILogger<AccountMonitor> logger;

    /// <summary>Name proposed to ollama.com for this server in place of the host name chosen by Ollama.</summary>
    private readonly string deviceName;

    /// <summary>Serialises account checks, since the pairing screen polls while a server restart triggers its own check.</summary>
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    /// <summary>Reachability seen at the previous status probe, to detect the moment the server comes up.</summary>
    private bool wasServerReachable;

    public AccountMonitor(IOllamaApiClient ollamaClient, ServerStatusMonitor statusMonitor, IOptions<OllamaOptions> options, ILogger<AccountMonitor> logger)
    {
        this.ollamaClient = ollamaClient;
        this.statusMonitor = statusMonitor;
        this.logger = logger;
        deviceName = options.Value.DeviceName;

        // A server that starts, restarts or goes down changes what is known about the account.
        statusMonitor.Changed += OnServerStatusChanged;
    }

    /// <summary>Raised after every account check and when the server goes down; handlers run on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Last answer of the server; null while unknown, that is before the first check or while the server is down.</summary>
    public AccountStatus? Current { get; private set; }

    /// <summary>Reason of the last failed check, typically ollama.com being unreachable from the server.</summary>
    public string? LastError { get; private set; }

    /// <summary>Asks the server which account its key is linked to, publishing the answer to every circuit.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // A check already running is waited for, so its caller still gets a published answer.
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            // A server that is down cannot tell, so the account becomes unknown rather than signed out.
            if (!statusMonitor.Current.IsReachable)
            {
                Current = null;
                LastError = null;
                return;
            }

            // The server answers with the profile or with the page that links its key, listed under the configured name.
            AccountStatus accountStatus = await ollamaClient.GetAccountStatusAsync(cancellationToken);
            Current = accountStatus.WithDeviceName(deviceName);
            LastError = null;
        }
        catch (Exception accountError) when (accountError is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The previous answer stays visible next to the reason the check failed.
            logger.LogWarning(accountError, "Checking the ollama.com account failed");
            LastError = accountError.Message;
        }
        finally
        {
            refreshGate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Unlinks the server key from its account and publishes the signed-out state with the new sign-in link.</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        // A refused sign-out reaches the caller, since the account is still linked.
        await ollamaClient.SignOutAsync(cancellationToken);

        // The fresh check returns the link needed to pair again.
        await RefreshAsync(cancellationToken);
    }

    /// <summary>Stops following the server status when the application shuts down.</summary>
    public void Dispose() => statusMonitor.Changed -= OnServerStatusChanged;

    /// <summary>Checks the account when the server comes up and forgets it when the server goes down.</summary>
    private void OnServerStatusChanged()
    {
        bool isServerReachable = statusMonitor.Current.IsReachable;
        bool hasServerComeUp = isServerReachable && !wasServerReachable;
        wasServerReachable = isServerReachable;

        // A server that just came up may run with a different key, so the account is asked again.
        if (hasServerComeUp)
        {
            _ = RefreshAsync();
            return;
        }

        // A server that went down leaves the account unknown until it is back.
        if (!isServerReachable && (Current is not null || LastError is not null))
        {
            Current = null;
            LastError = null;
            Changed?.Invoke();
        }
    }
}
