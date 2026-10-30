# CLAUDE.md

Guida operativa per Claude Code su questo repository.

## Cos'è Ollamazing

Dashboard Blazor Server (.NET 10, Fluent UI Blazor 5) che fa da frontend a un'installazione Ollama: catalogo di ollama.com a faccette, download, controllo del server, modelli locali, account ollama.com.

## Struttura

```
Ollamazing.slnx
Ollamazing.Server/     class library senza UI né ASP.NET: Lifecycle, Status, Account, Pulls, Configuration, Extensions
Ollamazing.Web/        web app: Catalog (scraping ollama.com, faccette) e Components (Layout, Pages, Shared)
```

- Web dipende da Server, mai il contrario; Server dipende solo da `Microsoft.Extensions.*` e OllamaSharp.
- I servizi del server si registrano con `AddOllamazingServer(configuration)`.
- Il namespace segue la cartella.
- Versione (`VersionPrefix`) e proprietà comuni stanno in `Directory.Build.props`.

## Comandi

```bash
dotnet build Ollamazing.slnx
dotnet run --project Ollamazing.Web          # http://localhost:5222
```

## Principi

- **Multipiattaforma:** niente systemd, sudo, launchctl, taskkill o shell; il server si gestisce con `System.Diagnostics.Process`.
- **OllamaSharp è l'interlocutore:** si usano `IOllamaApiClient` e i suoi tipi; ciò che manca si aggiunge come extension method in `Ollamazing.Server/Extensions/OllamaClientExtensions.cs`.
- **UI solo Fluent UI Blazor 5** (versione fissata): componenti Fluent, aspetto dal tema in `MainLayout`, solo API pubblica documentata (mai interni, `.fluent-*`, shadow DOM). Dove Fluent non ha un controllo lo si fa in Razor sui token Fluent (`Shared/DualRangeSlider`).
- **Faccette:** AND tra faccette, OR dentro la stessa; conteggi disgiuntivi.

## Coordinate

- **ollama.com non ha API pubblica:** `/search?page=N` vuole `HX-Request: true`; dimensioni in `/library/<nome>/tags`; tariffe in `/pricing#model-pricing`. Il markup si gestisce solo in `OllamaCatalogScraper.cs`; il catalogo si persiste in SQLite (`CatalogStore.cs`, schema versionato con `PRAGMA user_version`) in `ollamazing.db` nella cartella dati dell'utente, spostabile con `Catalog:DatabasePath`.
- **Account:** è legato alla chiave ed25519 del server in `~/.ollama`. Endpoint locali: `POST /api/me`, `POST /api/signout`, `GET /api/balance`, `GET /api/usage`. Il pairing apre il `signin_url` (solo `https://ollama.com`) e interroga `/api/me` finché il server risulta collegato.
- **Stato condiviso:** `CatalogService`, `PullManager`, `ServerStatusMonitor`, `AccountMonitor` sono singleton con eventi; i componenti ereditano `LiveComponentBase` e usano `Watch(...)`.
- **Razor:** niente stringhe tra doppi apici dentro `@onclick="..."`.
- **Lingue:** UI e commenti in inglese, commenti secondo la skill `code-commentation`.
- **Shell:** `rm`, `cp` e `mv` sono alias interattivi, si usano `/bin/rm -f`, `/bin/cp -f`, `/bin/mv -f`.
