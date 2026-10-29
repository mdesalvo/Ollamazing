using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Ollamazing.Web.Ollama;

/// <summary>Link between the local server and an ollama.com account, as reported by <c>POST /api/me</c>.</summary>
/// <param name="User">Profile of the signed-in account; null when the server key is not linked to any account.</param>
/// <param name="SignInUrl">ollama.com page that links the server key to the account opening it; present only when signed out.</param>
/// <remarks>The link carries only the public half of the server key; the private half never leaves <c>~/.ollama</c>.</remarks>
public sealed record AccountStatus(UserResponse? User, string? SignInUrl)
{
    /// <summary>True when cloud requests are signed for an account, which is what paid cloud models require.</summary>
    public bool IsSignedIn => User is not null;

    /// <summary>Name under which ollama.com lists this server among the devices of the account; null without a sign-in link.</summary>
    public string? DeviceName => ReadSignInParameter("name");

    /// <summary>OpenSSH fingerprint of the server key being linked, as <c>ssh-keygen -l</c> prints it.</summary>
    /// <remarks>Comparing it with the key listed on ollama.com confirms that the approved device is this server.</remarks>
    public string? KeyFingerprint
    {
        get
        {
            // The link carries the OpenSSH public key line ("ssh-ed25519 AAAA…") encoded as unpadded base64.
            if (ReadSignInParameter("key") is not { } encodedKeyLine)
                return null;
            try
            {
                // Both base64 alphabets are accepted, since the padding and URL-safe variants differ only in a few characters.
                string standardBase64 = encodedKeyLine.Replace('-', '+').Replace('_', '/');
                standardBase64 = standardBase64.PadRight(standardBase64.Length + (4 - standardBase64.Length % 4) % 4, '=');
                string[] keyLineParts = Encoding.ASCII.GetString(Convert.FromBase64String(standardBase64)).Split(' ');

                // The fingerprint hashes the binary key blob, the second field of the line, as OpenSSH does.
                byte[] keyBlob = Convert.FromBase64String(keyLineParts[1]);
                return $"SHA256:{Convert.ToBase64String(SHA256.HashData(keyBlob)).TrimEnd('=')}";
            }
            catch (Exception decodeError) when (decodeError is FormatException or IndexOutOfRangeException)
            {
                // A key in an unexpected shape is not shown rather than shown wrong.
                return null;
            }
        }
    }

    /// <summary>Returns the same status with the device name of the sign-in link replaced, leaving the key untouched.</summary>
    /// <remarks>The name is only the label ollama.com shows for the key, so changing it does not change what gets authorized.</remarks>
    public AccountStatus WithDeviceName(string deviceName)
    {
        // A signed-in status has no link, and an empty name keeps the one proposed by the server.
        if (SignInUrl is null || string.IsNullOrWhiteSpace(deviceName) || !Uri.TryCreate(SignInUrl, UriKind.Absolute, out Uri? signInUri))
            return this;

        // Every other parameter, the key above all, is carried over unchanged and in the same order.
        Dictionary<string, string?> signInParameters = QueryHelpers.ParseQuery(signInUri.Query)
            .ToDictionary(parameter => parameter.Key, parameter => (string?)parameter.Value.ToString());
        signInParameters["name"] = deviceName.Trim();
        return this with { SignInUrl = new UriBuilder(signInUri) { Query = QueryString.Create(signInParameters).ToUriComponent() }.Uri.AbsoluteUri };
    }

    /// <summary>Reads one query parameter of the sign-in link, which is where the server describes itself to ollama.com.</summary>
    private string? ReadSignInParameter(string parameterName)
    {
        // Signed-in servers have no link to read from.
        if (SignInUrl is null || !Uri.TryCreate(SignInUrl, UriKind.Absolute, out Uri? signInUri))
            return null;
        return QueryHelpers.ParseQuery(signInUri.Query).TryGetValue(parameterName, out var parameterValues) ? parameterValues.FirstOrDefault() : null;
    }
}

/// <summary>Profile of the ollama.com account the server signs its cloud requests for.</summary>
public sealed record UserResponse
{
    /// <summary>Stable identifier of the account on ollama.com.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Address the account is registered with on ollama.com.</summary>
    [JsonPropertyName("email")]
    public string? Email { get; init; }

    /// <summary>ollama.com user name, the one shown in model namespaces.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Profile picture hosted by ollama.com.</summary>
    [JsonPropertyName("avatarurl")]
    public string? AvatarUrl { get; init; }

    /// <summary>Given name from the profile, optional on ollama.com.</summary>
    [JsonPropertyName("firstname")]
    public string? FirstName { get; init; }

    /// <summary>Family name from the profile, optional on ollama.com.</summary>
    [JsonPropertyName("lastname")]
    public string? LastName { get; init; }

    /// <summary>Subscription plan, such as "free"; it decides the usage included every period.</summary>
    [JsonPropertyName("plan")]
    public string? Plan { get; init; }
}

/// <summary>Credits available for cloud usage, as reported by <c>GET /api/balance</c>.</summary>
public sealed record BalanceResponse
{
    /// <summary>Usage included in the plan, renewed every period.</summary>
    [JsonPropertyName("included")]
    public IncludedBalance? Included { get; init; }

    /// <summary>Pay-as-you-go credits bought on ollama.com, spent once the included usage is exhausted.</summary>
    [JsonPropertyName("purchased")]
    public PurchasedBalance? Purchased { get; init; }
}

/// <summary>Plan allowance of the current billing period and how much of it is left.</summary>
public sealed record IncludedBalance
{
    /// <summary>Part of the allowance still available in the current period, in US dollars.</summary>
    [JsonPropertyName("balance_usd")]
    public decimal BalanceUsd { get; init; }

    /// <summary>Whole allowance granted by the plan for one period, in US dollars.</summary>
    [JsonPropertyName("allowance_usd")]
    public decimal AllowanceUsd { get; init; }

    /// <summary>Billing period the allowance belongs to; it renews when the period ends.</summary>
    [JsonPropertyName("period")]
    public BalancePeriod? Period { get; init; }
}

/// <summary>Billing period the included allowance refers to.</summary>
public sealed record BalancePeriod
{
    /// <summary>Start of the period, when the allowance was last renewed.</summary>
    [JsonPropertyName("from")]
    public DateTimeOffset From { get; init; }

    /// <summary>End of the period, when the allowance renews.</summary>
    [JsonPropertyName("until")]
    public DateTimeOffset Until { get; init; }
}

/// <summary>Remaining amount of the credits bought on ollama.com.</summary>
public sealed record PurchasedBalance
{
    /// <summary>Credits left in US dollars, decreasing as paid cloud usage is billed.</summary>
    [JsonPropertyName("balance_usd")]
    public decimal BalanceUsd { get; init; }
}

/// <summary>Cloud consumption of the account over a time range, as reported by <c>GET /api/usage</c>.</summary>
public sealed record UsageResponse
{
    /// <summary>Range the figures cover: "24h", "7d" or "30d".</summary>
    [JsonPropertyName("range")]
    public string? Range { get; init; }

    /// <summary>Start of the range, aligned by ollama.com to the hour or the day.</summary>
    [JsonPropertyName("from")]
    public DateTimeOffset From { get; init; }

    /// <summary>End of the range, which is the moment of the request.</summary>
    [JsonPropertyName("until")]
    public DateTimeOffset Until { get; init; }

    /// <summary>Counters summed over the whole range.</summary>
    [JsonPropertyName("totals")]
    public UsageTotals? Totals { get; init; }
}

/// <summary>Counters shared by the totals of a range and by each model within it.</summary>
public abstract record UsageFigures
{
    /// <summary>Number of cloud requests served.</summary>
    [JsonPropertyName("request_count")]
    public long RequestCount { get; init; }

    /// <summary>Amount charged in US dollars; zero while the included allowance covers the usage.</summary>
    [JsonPropertyName("usage_usd")]
    public decimal UsageUsd { get; init; }

    /// <summary>Prompt tokens sent to the cloud model.</summary>
    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; init; }

    /// <summary>Prompt tokens served from the cache, billed at the lower cached rate.</summary>
    [JsonPropertyName("cached_input_tokens")]
    public long CachedInputTokens { get; init; }

    /// <summary>Generated tokens, billed at the output rate.</summary>
    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; init; }
}

/// <summary>Counters of the whole range, broken down by model when requested.</summary>
public sealed record UsageTotals : UsageFigures
{
    /// <summary>Counters of each cloud model used in the range; empty unless the breakdown was requested.</summary>
    [JsonPropertyName("groups")]
    public IReadOnlyList<ModelUsage> Groups { get; init; } = [];
}

/// <summary>Counters of a single cloud model within the range.</summary>
public sealed record ModelUsage : UsageFigures
{
    /// <summary>Cloud model the counters belong to, as named by ollama.com.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }
}
