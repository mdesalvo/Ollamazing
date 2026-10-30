<p align="center"><img src="Ollamazing.Web/wwwroot/Images/ollamazing.jpg" alt="Ollamazing: ollama handling made amazing" /></p>

Ollamazing puts your whole [Ollama](https://ollama.com) installation in one dashboard. Stop juggling terminal commands and browser tabs: find the right model in the ollama.com catalog with faceted filters, pull it with live progress, start and stop the server, keep your local models tidy and link your ollama.com cloud account, all in a few clicks. **Built with ❤️ using .NET 10, Blazor Server, [OllamaSharp](https://github.com/awaescher/OllamaSharp) and [Fluent UI Blazor](https://www.fluentui-blazor.net)**.

## What you can do

**Find the right model.** Browse every model published on ollama.com and narrow the list by class, pricing, capabilities, parameters, memory and price per million tokens. Every tag tells you at a glance whether it fits your machine; models that just arrived carry a "new" badge.

**Get it in one click.** Pull any model with live progress. Downloads keep going while you move around the app; you can cancel them or try again.

**Put it to work.** "Use this model" hands you ready-to-copy snippets for the terminal, curl, Python and any OpenAI-compatible client.

**Keep your models tidy.** See what is installed and what sits in memory, warm a model up before you need it, update it or remove it.

**Stay in control of Ollama.** Start, restart and stop the server; see at a glance whether it is up, which version runs and how it uses your memory.

**Bring in the cloud.** Link Ollama to your ollama.com account to run cloud models, then follow credits, plan and usage model by model.

## Getting started

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Ollama](https://ollama.com/download). Ollamazing runs on Windows, macOS and Linux.

```bash
dotnet run --project Ollamazing.Web
```

Then open http://localhost:5222. If Ollama is not running yet, start it from the Server page.

## Your ollama.com account

Cloud models are billed to the ollama.com account linked to your Ollama server. Linking works like `ollama signin`: Ollamazing opens ollama.com, where you sign in and approve this device. Your password never passes through Ollamazing, while the key that identifies your server never leaves your machine. Signing out removes the device from your account.

## Settings

Everything lives in `Ollamazing.Web/appsettings.json`, with defaults that suit a local Ollama. These are the settings you are most likely to change:

| Setting | What it does |
|---|---|
| `Ollama:BaseUrl` | Where Ollama listens (`http://localhost:11434` by default) |
| `Ollama:WarmUpKeepAlive` | How long a warmed-up model stays in memory |
| `ServerControl:StartOnLaunch` | Starts Ollama together with Ollamazing |
| `ServerControl:Environment` | Extra variables for the Ollama server that Ollamazing starts, such as `OLLAMA_MODELS` |
| `Catalog:DatabasePath` | Where the catalog is kept (your user data folder by default) |

<details>
<summary>All settings</summary>

| Section | Key | Meaning |
|---|---|---|
| `Ollama` | `BaseUrl` | Address of the Ollama API |
| | `WarmUpKeepAlive` | How long a warmed-up model stays loaded (`30m`; `-1` keeps it forever) |
| | `StatusPollSeconds` | How often the server status is checked |
| | `DeviceName` | Name under which ollama.com lists this server once linked (`Ollamazing` by default; empty keeps the host name) |
| `ServerControl` | `ExecutablePath` | `ollama` executable used to start the server (looked up in PATH) |
| | `Environment` | Extra variables for the server that Ollamazing starts |
| | `StartOnLaunch` | Starts the server when Ollamazing boots and none is running |
| | `StopOnExit` | Stops the server that Ollamazing started when Ollamazing exits |
| | `LogFile` | Log of a server started elsewhere; empty uses the default of the Ollama desktop app |
| `Catalog` | `DatabasePath` | Catalog database; empty uses the user data folder (`~/.local/share/Ollamazing` on Linux, `%LOCALAPPDATA%\Ollamazing` on Windows, `~/Library/Application Support/Ollamazing` on macOS) |
| | `CacheTtlHours` | Age after which the catalog is refreshed from ollama.com |
| | `MaxConcurrentRequests` | Parallel requests to ollama.com while sizes are loaded |

</details>