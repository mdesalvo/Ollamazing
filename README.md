# Llamazing

Llamazing is a Blazor Server dashboard for [Ollama](https://ollama.com): browse the official model catalog with faceted filters, pull models with live progress, start and stop the Ollama server, and keep your local models tidy by updating, deleting or warming them up in memory. Built with .NET 10 and [OllamaSharp](https://github.com/awaescher/OllamaSharp).

## Features

- **Catalog**: every model published on ollama.com, browsed by facets. Facets combine with AND, while options inside the same facet combine with OR. Every option shows how many models it would match.
  - **Class**: local weights, cloud, or both.
  - **Pricing**: free (local weights or free cloud tier) or paid (cloud usage).
  - **Capabilities**: tools, vision, thinking, embedding, audio…
  - **Memory size**: a min/max slider over the download size of each tag, with a "fits my RAM" shortcut.
- **Downloads**: pulls run in background with live progress, survive page navigation, and can be cancelled and retried. Any reference can be typed by hand.
- **Local models**: list installed models with size, parameters, quantization and residency. You can update (re-pull), delete, warm up (load in memory), unload and inspect each one.
- **Server**: API reachability and version, the hosting process, loaded models with GPU share and expiry, and the server log, plus start, stop and restart.

## Requirements

- .NET 10 SDK
- Ollama installed and reachable (default `http://localhost:11434`)
- Works on Windows, macOS and Linux

## Run

```bash
dotnet run --project Llamazing.Web
```

Then open http://localhost:5222.

## Configuration

All settings live in `Llamazing.Web/appsettings.json`.

| Section | Key | Meaning |
|---|---|---|
| `Ollama` | `BaseUrl` | Address of the Ollama API |
| | `WarmUpKeepAlive` | How long a warmed-up model stays loaded (`30m`, `-1` = forever) |
| | `StatusPollSeconds` | Interval of the server status probe |
| `ServerControl` | `ExecutablePath` | `ollama` executable used for `ollama serve` (looked up in PATH) |
| | `Environment` | Extra variables for the server Llamazing starts, e.g. `OLLAMA_MODELS` |
| | `StartOnLaunch` | Start the server when Llamazing boots and none is running |
| | `StopOnExit` | Stop the server Llamazing started when Llamazing exits |
| | `LogFile` | Log of a server started elsewhere; empty uses the Ollama desktop app default |
| `Catalog` | `CacheTtlHours` | Age after which the cached catalog is refreshed from ollama.com |
| | `MaxConcurrentRequests` | Parallel requests to ollama.com while loading tag sizes |

### Starting and stopping the server

Llamazing manages Ollama as a plain OS process, the same way on every platform:

- **Start** launches `ollama serve` as a child process. Its output is captured into the log viewer.
- **Stop** terminates the running Ollama server, whether Llamazing started it or not, provided that the process belongs to your account.

A server running under another account, such as a system service, cannot be stopped from Llamazing. Stop it with the tools of your OS, then let Llamazing start its own instance.

## How the catalog is read

ollama.com offers no public catalog API, so Llamazing reads the public HTML pages:

- `/search` provides the model list, capabilities and cloud pricing.
- `/library/<model>/tags` provides per-tag sizes.

Requests are throttled and the result is cached in `Llamazing.Web/data/catalog-cache.json`.

## License

Apache 2.0
