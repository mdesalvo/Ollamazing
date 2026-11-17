<p align="center"><img src="Ollamazing.Web/wwwroot/Images/ollamazing.jpg" alt="Ollamazing: ollama handling made amazing" /></p>

<p align="center">
  <a href="https://github.com/mdesalvo/Ollamazing/releases/latest"><img src="https://img.shields.io/github/v/release/mdesalvo/Ollamazing?color=lightblue" alt="Latest release"/></a>
  <img src="https://img.shields.io/badge/.NET-10-FFFFFF?color=lightblue" alt=".NET 10"/>
  <img src="https://img.shields.io/badge/OllamaSharp-FFFFFF?logo=nuget&color=lightblue" alt="OllamaSharp"/>
  <img src="https://img.shields.io/badge/Ollama-FFFFFF?logo=ollama&color=lightblue" alt="Ollama"/>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/mdesalvo/Ollamazing?color=lightblue" alt="Apache 2.0 license"/></a>
</p>

Ollamazing puts your whole [Ollama](https://ollama.com) installation in one dashboard. Stop juggling terminal commands and browser tabs: find the right model in the ollama.com catalog, pull it with live progress, run and update the server, keep your local models tidy and link your ollama.com cloud account, all in a few clicks.

## What you can do

<table>
<tr>
<td width="50%" valign="top">

🔍 **Find the right model**<br/>
Browse every model on ollama.com and narrow the list by class, capabilities, parameters, memory and price per million tokens. Each tag tells you whether it fits the GPU or the RAM that Ollama runs on.

</td>
<td width="50%" valign="top">

⬇️ **Get it in one click**<br/>
Pull any model with live progress, pause it and resume it later. Downloads keep going while you move around the app.

</td>
</tr>
<tr>
<td valign="top">

🧑‍💻 **Put it to work**<br/>
"Use this model" hands you ready-to-copy snippets for the terminal, curl, Python and any OpenAI-compatible client.

</td>
<td valign="top">

📦 **Keep your models tidy**<br/>
See what is installed and what sits in memory; warm a model up before you need it, update it or remove it.

</td>
</tr>
<tr>
<td valign="top">

⚙️ **Stay in control of Ollama**<br/>
Start and stop the server, see its version and hardware at a glance and update Ollama itself in place when a new release is out.

</td>
<td valign="top">

☁️ **Bring in the cloud**<br/>
Link Ollama to your ollama.com account to run cloud models, then follow credits, plan and usage model by model.

</td>
</tr>
</table>

What changed in this version is in the [release notes](https://github.com/mdesalvo/Ollamazing/releases/latest).

## Getting started

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Ollama](https://ollama.com/download). Ollamazing runs on Windows, macOS and Linux.

```bash
dotnet run --project Ollamazing.Web
```

Then open http://localhost:5222. There is no need to start Ollama first: Ollamazing starts it for you when none is running.

## Your ollama.com account

Cloud models are billed to the ollama.com account linked to your Ollama server. Linking works like `ollama signin`: Ollamazing opens ollama.com, where you sign in and approve this device. Your password never passes through Ollamazing, while the key that identifies your server never leaves your machine. Signing out removes the device from your account.

## Settings

Everything lives in `Ollamazing.Web/appsettings.json`, with defaults that suit a local Ollama. These are the settings you are most likely to change:

| Setting | What it does |
|---|---|
| `Ollama:BaseUrl` | Where Ollama listens (`http://localhost:11434` by default) |
| `Ollama:WarmUpKeepAlive` | How long a warmed-up model stays in memory (an hour by default) |
| `ServerControl:StartOnLaunch` | Starts Ollama together with Ollamazing when none is running (on by default) |
| `ServerControl:Environment` | Extra variables for the Ollama server that Ollamazing starts, such as `OLLAMA_MODELS` |
| `Catalog:DatabasePath` | Where the catalog is kept (your user data folder by default) |

<details>
<summary>All settings</summary>

| Section | Key | Meaning |
|---|---|---|
| `Ollama` | `BaseUrl` | Address of the Ollama API |
| | `WarmUpKeepAlive` | How long a warmed-up model stays loaded (`60m`; `-1` keeps it forever) |
| | `StatusPollSeconds` | How often the server status is checked |
| | `DeviceName` | Name under which ollama.com lists this server once linked (`Ollamazing` by default; empty keeps the host name) |
| `ServerControl` | `ExecutablePath` | `ollama` executable used to start the server (looked up in PATH) |
| | `Environment` | Extra variables for the server that Ollamazing starts |
| | `StartOnLaunch` | Starts the server when Ollamazing boots, unless `BaseUrl` points to another machine or a server already runs or answers there |
| | `StopOnExit` | Stops the server that Ollamazing started when Ollamazing exits |
| | `LogFile` | Log of a server started elsewhere; empty uses the default of the Ollama desktop app |
| `Catalog` | `DatabasePath` | Catalog database; empty uses the user data folder (`~/.local/share/Ollamazing` on Linux, `%LOCALAPPDATA%\Ollamazing` on Windows, `~/Library/Application Support/Ollamazing` on macOS) |
| | `CacheTtlHours` | Age after which the catalog is refreshed from ollama.com |
| | `MaxConcurrentRequests` | Parallel requests to ollama.com while sizes are loaded |

</details>
