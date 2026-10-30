# Ollamazing

Ollamazing is a Blazor Server dashboard for [Ollama](https://ollama.com): browse the official model catalog with faceted filters, pull models with live progress, start and stop the Ollama server, and keep your local models tidy by updating, deleting or warming them up in memory. Built with .NET 10, [OllamaSharp](https://github.com/awaescher/OllamaSharp) and [Fluent UI Blazor](https://www.fluentui-blazor.net) 5.

## Features

- **Catalog**: every model published on ollama.com, browsed by facets. Facets combine with AND, while options inside the same facet combine with OR. Every option shows how many models it would match.
  - **Class**: local weights, cloud, or both.
  - **Pricing**: free (local weights or free cloud tier) or paid (cloud usage).
  - **Capabilities**: tools, vision, thinking, embedding, audio…
  - **Memory size**: a min/max slider over the download size of each tag, with a "fits my RAM" shortcut.
- **Downloads**: pulls run in background with live progress, survive page navigation, and can be cancelled and retried. Any reference can be typed by hand.
- **Local models**: list installed models with size, parameters, quantization and residency. You can update (re-pull), delete, warm up (load in memory), unload and inspect each one.
- **Server**: API reachability and version, the hosting process, loaded models with GPU share and expiry, and the server log, plus start, stop and restart.
- **Account**: link the server to your ollama.com account, see purchased credits, plan allowance and cloud usage per model, and sign out.

## ollama.com account

Cloud models run on ollama.com and are billed to the account linked to the local server. The link uses the same handshake as `ollama signin`:

1. The server holds an ed25519 key pair in `~/.ollama`. The private key never leaves it.
2. On the Account page, **Link account on ollama.com** opens ollama.com with the public key and the device name. You sign in there, never through Ollamazing, and approve the device.
3. Ollamazing polls the server until the link is active, then shows profile, credits and usage.

The page shows the SHA256 fingerprint of the key being linked, in the format of `ssh-keygen -l`, so you can check it against the device listed on ollama.com. Sign-in links are opened only when they point to `https://ollama.com`.

**Sign out** revokes the server key on ollama.com, so the device disappears from the account and cloud models stop working. Linking again repeats the same handshake with the same key.

## Requirements

- .NET 10 SDK
- Ollama installed and reachable (default `http://localhost:11434`)
- Works on Windows, macOS and Linux

## Run

```bash
dotnet run --project Ollamazing.Web
```

Then open http://localhost:5222.

## Configuration

All settings live in `Ollamazing.Web/appsettings.json`.

| Section | Key | Meaning |
|---|---|---|
| `Ollama` | `BaseUrl` | Address of the Ollama API |
| | `WarmUpKeepAlive` | How long a warmed-up model stays loaded (`30m`, `-1` = forever) |
| | `StatusPollSeconds` | Interval of the server status probe |
| | `DeviceName` | Name under which ollama.com lists this server once linked (default `Ollamazing`; empty keeps the host name) |
| `ServerControl` | `ExecutablePath` | `ollama` executable used for `ollama serve` (looked up in PATH) |
| | `Environment` | Extra variables for the server Ollamazing starts, e.g. `OLLAMA_MODELS` |
| | `StartOnLaunch` | Start the server when Ollamazing boots and none is running |
| | `StopOnExit` | Stop the server Ollamazing started when Ollamazing exits |
| | `LogFile` | Log of a server started elsewhere; empty uses the Ollama desktop app default |
| `Catalog` | `CacheTtlHours` | Age after which the cached catalog is refreshed from ollama.com |
| | `MaxConcurrentRequests` | Parallel requests to ollama.com while loading tag sizes |

### Starting and stopping the server

Ollamazing manages Ollama as a plain OS process, the same way on every platform:

- **Start** launches `ollama serve` as a child process. Its output is captured into the log viewer.
- **Stop** terminates the running Ollama server, whether Ollamazing started it or not, provided that the process belongs to your account.

A server running under another account, such as a system service, cannot be stopped from Ollamazing. Stop it with the tools of your OS, then let Ollamazing start its own instance.

## How the catalog is read

ollama.com offers no public catalog API, so Ollamazing reads the public HTML pages:

- `/search` provides the model list, capabilities and the cloud usage level.
- `/library/<model>/tags` provides per-tag sizes.
- `/pricing` provides the pay-as-you-go cloud rates per million tokens (input, cached input, output, off-peak), shown on the cards of the models it lists.

Requests are throttled and the result is cached in `Ollamazing.Web/data/catalog-cache.json`.

## License

Apache 2.0
