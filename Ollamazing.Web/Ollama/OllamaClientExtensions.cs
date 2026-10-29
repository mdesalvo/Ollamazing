using OllamaSharp;
using OllamaSharp.Models;

namespace Ollamazing.Web.Ollama;

/// <summary>Operations missing from OllamaSharp, built on <see cref="IOllamaApiClient"/> in the style of its own extensions.</summary>
public static class OllamaClientExtensions
{
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
}
