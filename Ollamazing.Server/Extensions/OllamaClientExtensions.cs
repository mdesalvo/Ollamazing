using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OllamaSharp;
using OllamaSharp.Models;
using Ollamazing.Server.Account;

namespace Ollamazing.Server.Extensions;

/// <summary>Operations missing from OllamaSharp, built on <see cref="IOllamaApiClient"/> in the style of its own extensions.</summary>
public static class OllamaClientExtensions
{
    /// <summary>Connection for the account endpoints, addressed through the server URI of the client.</summary>
    /// <remarks>OllamaSharp keeps its own HTTP client private, so the account calls cannot travel through it.</remarks>
    private static readonly HttpClient AccountHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Only host allowed to receive the user for the sign-in, so a tampered server cannot redirect to a look-alike page.</summary>
    private const string TrustedSignInHost = "ollama.com";

    /// <summary>Tells whether the server key is linked to an ollama.com account, returning the sign-in link when it is not.</summary>
    /// <remarks>The server answers by asking ollama.com, so a network failure there surfaces as an exception.</remarks>
    public static async Task<AccountStatus> GetAccountStatusAsync(this IOllamaApiClient client, CancellationToken cancellationToken = default)
    {
        // The server checks its key against ollama.com before answering.
        using HttpResponseMessage response = await AccountHttpClient.PostAsync(new Uri(client.Uri, "api/me"), null, cancellationToken);

        // An unlinked key is reported as unauthorized, together with the page that links it.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            SignInRequiredResponse? signInRequired = await response.Content.ReadFromJsonAsync<SignInRequiredResponse>(cancellationToken);

            // The user will authenticate on the linked page, so it must be ollama.com over HTTPS and nothing else.
            if (signInRequired?.SignInUrl is { } signInUrl && !IsTrustedSignInUrl(signInUrl))
                throw new HttpRequestException($"The server proposed a sign-in page outside https://{TrustedSignInHost}; it was not opened.", null, response.StatusCode);
            return new AccountStatus(null, signInRequired?.SignInUrl);
        }

        // Any other failure is not a sign-in matter and is reported to the caller.
        await EnsureAccountSuccessAsync(response, cancellationToken);

        // A linked key yields the profile of the account it belongs to.
        UserResponse? user = await response.Content.ReadFromJsonAsync<UserResponse>(cancellationToken);
        return new AccountStatus(user, null);
    }

    /// <summary>Unlinks the server key from its ollama.com account, so cloud models stop working until the next sign-in.</summary>
    public static async Task SignOutAsync(this IOllamaApiClient client, CancellationToken cancellationToken = default)
    {
        // The server revokes its key on ollama.com and forgets the account.
        using HttpResponseMessage response = await AccountHttpClient.PostAsync(new Uri(client.Uri, "api/signout"), null, cancellationToken);

        // A refused sign-out leaves the account linked, which the caller must know.
        await EnsureAccountSuccessAsync(response, cancellationToken);
    }

    /// <summary>Reads the credits left for cloud usage: the plan allowance of the period and the purchased credits.</summary>
    /// <remarks>Requires a signed-in server; a signed-out one answers with an error.</remarks>
    public static async Task<BalanceResponse> GetBalanceAsync(this IOllamaApiClient client, CancellationToken cancellationToken = default)
    {
        // The server relays the balance kept by ollama.com for the linked account.
        using HttpResponseMessage response = await AccountHttpClient.GetAsync(new Uri(client.Uri, "api/balance"), cancellationToken);

        // A signed-out server or an unreachable ollama.com leaves no balance to show.
        await EnsureAccountSuccessAsync(response, cancellationToken);

        // The body always carries both balances, possibly at zero.
        return await response.Content.ReadFromJsonAsync<BalanceResponse>(cancellationToken) ?? new BalanceResponse();
    }

    /// <summary>Reads the cloud consumption of the account over a range, broken down by model.</summary>
    /// <param name="range">Time window accepted by ollama.com: "24h", "7d" or "30d".</param>
    /// <remarks>Requires a signed-in server; a signed-out one answers with an error.</remarks>
    public static async Task<UsageResponse> GetUsageAsync(this IOllamaApiClient client, string range, CancellationToken cancellationToken = default)
    {
        // The per-model breakdown is always requested, since totals alone do not tell which model spent the credits.
        using HttpResponseMessage response = await AccountHttpClient.GetAsync(new Uri(client.Uri, $"api/usage?range={Uri.EscapeDataString(range)}&group_by=model"), cancellationToken);

        // An unsupported range is rejected by ollama.com with an explanatory message.
        await EnsureAccountSuccessAsync(response, cancellationToken);

        // The breakdown by model is carried inside the totals.
        return await response.Content.ReadFromJsonAsync<UsageResponse>(cancellationToken) ?? new UsageResponse();
    }

    /// <summary>Accepts a sign-in link only when it points to ollama.com over HTTPS, without credentials or a custom port.</summary>
    private static bool IsTrustedSignInUrl(string signInUrl)
        => Uri.TryCreate(signInUrl, UriKind.Absolute, out Uri? signInUri)
           && signInUri.Scheme == Uri.UriSchemeHttps
           && signInUri.IsDefaultPort
           && string.IsNullOrEmpty(signInUri.UserInfo)
           && signInUri.Host.Equals(TrustedSignInHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns a failed account call into an exception carrying the reason given by the server.</summary>
    private static async Task EnsureAccountSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // A successful answer needs no explanation.
        if (response.IsSuccessStatusCode)
            return;

        // Ollama explains failures in an "error" field; a body in another shape falls back to the status code.
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        string reason;
        try
        {
            reason = JsonSerializer.Deserialize<SignInRequiredResponse>(body)?.Error ?? body;
        }
        catch (JsonException)
        {
            // A plain-text body, such as "405 method not allowed", is already the reason.
            reason = body;
        }
        // The status code travels with the exception, so callers can tell a signed-out server from an outage.
        throw new HttpRequestException($"Ollama answered {(int)response.StatusCode}: {reason.Trim()}", null, response.StatusCode);
    }

    /// <summary>Error body of the account endpoints, which adds the sign-in link when the key is not linked.</summary>
    private sealed record SignInRequiredResponse
    {
        /// <summary>Reason of the failure, such as "unauthorized" or "invalid credentials".</summary>
        [JsonPropertyName("error")]
        public string? Error { get; init; }

        /// <summary>ollama.com page that links the server key; present only on the unauthorized answer of the profile call.</summary>
        [JsonPropertyName("signin_url")]
        public string? SignInUrl { get; init; }
    }

    /// <summary>Loads a model into memory with an empty request, so the first real prompt does not pay the load time.</summary>
    /// <param name="keepAlive">How long the model stays resident, in Ollama keep_alive syntax (e.g. "30m", "-1" for ever).</param>
    /// <remarks>Counterpart of OllamaSharp's RequestModelUnloadAsync, which evicts a model the same way with a zero keep_alive.</remarks>
    public static async Task RequestModelLoadAsync(this IOllamaApiClient client, string model, string keepAlive, CancellationToken cancellationToken = default)
    {
        // Embedding-only models reject generate requests and are loaded through the embed endpoint instead.
        ShowModelResponse modelDescription = await client.ShowModelAsync(model, cancellationToken);
        IReadOnlyCollection<string> capabilities = modelDescription.Capabilities?.ToList() ?? [];
        if (capabilities.Contains("embedding") && !capabilities.Contains("completion"))
        {
            // An empty input loads the weights without computing anything meaningful.
            await client.EmbedAsync(new EmbedRequest { Model = model, Input = [""], KeepAlive = keepAlive }, cancellationToken);
            return;
        }

        // An empty prompt makes Ollama load the model and answer at once without generating tokens.
        GenerateRequest loadRequest = new() { Model = model, Prompt = "", KeepAlive = keepAlive, Stream = false };
        await foreach (GenerateResponseStream? _ in client.GenerateAsync(loadRequest, cancellationToken))
        {
            // The single response only confirms the load and carries nothing to keep.
        }
    }

    /// <summary>True for a cloud model, a pointer to ollama.com with no weights to load or to count as disk use.</summary>
    public static bool IsCloud(this Model model)
        => model.Name.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase) || model.Name.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase);
}
