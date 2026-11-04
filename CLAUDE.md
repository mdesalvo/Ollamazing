# CLAUDE.md

Working guide for Claude Code on this repository.

## What Ollamazing is

A Blazor Server dashboard (.NET 10, Fluent UI Blazor 5) that fronts an Ollama installation: faceted ollama.com catalog, downloads, server control, local models, ollama.com account.

## Structure

```
Ollamazing.slnx
Ollamazing.Server/     class library with no UI or ASP.NET: Lifecycle, Status, Account, Pulls, Updates, Configuration, Extensions
Ollamazing.Web/        web app: Catalog (ollama.com scraping, facets) and Components (Layout, Pages, Shared)
```

- Web depends on Server, never the reverse; Server depends only on `Microsoft.Extensions.*`, OllamaSharp and ZstdSharp.
- Server services are registered with `AddOllamazingServer(configuration)`.
- The namespace follows the folder.
- Version (`VersionPrefix`) and shared properties live in `Directory.Build.props`; package versions in `Directory.Packages.props` (Central Package Management), so the `.csproj` files have `PackageReference` without `Version`.

## Commands

```bash
dotnet build Ollamazing.slnx
dotnet run --project Ollamazing.Web          # http://localhost:5222
```

## Principles

- **Cross-platform:** no systemd, sudo, launchctl, taskkill or shell; the server is managed with `System.Diagnostics.Process`.
- **OllamaSharp is the counterpart:** use `IOllamaApiClient` and its types; whatever is missing is added as an extension method in `Ollamazing.Server/Extensions/OllamaClientExtensions.cs`.
- **UI is Fluent UI Blazor 5 only** (pinned version): Fluent components, look from the theme in `MainLayout`, documented public API only (never internals, `.fluent-*` or shadow DOM). Where Fluent has no control it is built in Razor on Fluent tokens (`Shared/DualRangeSlider`).
- **Visual language:** page actions Outline; the main action of a block Primary; actions inside cards and rows Small Outline (confirmations: Primary + Cancel Subtle); states with `FluentBadge` Tint (Brand installed, Success succeeded, Warning at the limit, Danger error, Informative cloud); server down → `ServerOfflineCard` where the page cannot work without it; only errors in `FluentMessageBar`; monospace only for code, logs, digests and configuration.
- **Facets:** AND across facets, OR within one; disjunctive counts.

## Coordinates

- **ollama.com has no public API:** `/search?page=N` needs `HX-Request: true`; sizes in `/library/<name>/tags`; prices in `/pricing#model-pricing`. Markup is handled only in `OllamaCatalogScraper.cs`; the catalog is persisted in SQLite (`CatalogStore.cs`, schema versioned with `PRAGMA user_version`) in `ollamazing.db` under the user data folder, movable with `Catalog:DatabasePath`.
- **Account:** tied to the server ed25519 key in `~/.ollama`. Local endpoints: `POST /api/me`, `POST /api/signout`, `GET /api/balance`, `GET /api/usage`. Pairing opens the `signin_url` (only `https://ollama.com`) and polls `/api/me` until the server is linked.
- **Updates:** latest release from `api.github.com/repos/ollama/ollama/releases/latest` (User-Agent required). `UpdateCapabilityDetector` decides whether the installation can be updated in place or only notified, at the first failing check (macOS is notify-only). `OllamaUpdater` downloads the main archive plus the GPU archives already installed (`-rocm`, `-mlx`, `-jetpack*`), verifies them against the release `sha256sum.txt`, stages them in `<install root>/.ollamazing-update` (never in the system temp folder), swaps `bin` and `lib/ollama` by rename and restores the previous version when the new server does not answer.
- **Shared state:** `CatalogService`, `PullManager`, `ServerStatusMonitor`, `AccountMonitor`, `OllamaUpdateMonitor`, `OllamaUpdater` are singletons with events; components inherit `LiveComponentBase` and use `Watch(...)`.
- **Razor:** no double-quoted strings inside `@onclick="..."`.
- **Languages:** this file, UI and comments in English; comments follow the `code-commentation` skill.
- **Shell:** `rm`, `cp` and `mv` are interactive aliases, so use `/bin/rm -f`, `/bin/cp -f`, `/bin/mv -f`.
