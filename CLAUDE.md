# CLAUDE.md

Guida operativa per Claude Code su questo repository.

## Cos'è Ollamazing

Dashboard Blazor Server (interattività server via SignalR, .NET 10) che fa da frontend a un'installazione Ollama:

- **Catalogo** dei modelli ufficiali di ollama.com, con navigazione a faccette: classe locale/cloud, prezzo gratuito/a pagamento, capability, dimensione con slider min/max.
- **Download** (pull) dei modelli in background, con avanzamento in tempo reale.
- **Server**: stato di Ollama, avvio/arresto/riavvio, modelli caricati in memoria, log.
- **Modelli locali**: aggiorna (re-pull), elimina, scalda (carica in memoria), scarica dalla memoria, dettagli.

## Struttura

```
Ollamazing.slnx              solution (formato slnx)
Ollamazing.Web/              unico progetto, niente cartella src/
  Configuration/            opzioni (sezioni Ollama, ServerControl, Catalog di appsettings.json)
  Catalog/                  scraping HTML di ollama.com (AngleSharp), cache su disco, motore a faccette
  Ollama/                   PullManager (download in background) + extension method su IOllamaApiClient
  Server/                   controllo del processo `ollama serve` + monitor dello stato condiviso
  Components/               UI Razor: Layout, Pages (*Page.razor), Shared
```

## Comandi

```bash
dotnet build Ollamazing.slnx
dotnet run --project Ollamazing.Web          # http://localhost:5222
```

## Principi (decisi con l'utente)

- **Multipiattaforma, mai scappatoie di sistema.** Deve funzionare identico su Windows, macOS e Linux.
  - Niente systemd, sudo, launchctl, taskkill o shell.
  - Il server si gestisce con `System.Diagnostics.Process`: start avvia `ollama serve` come processo figlio, stop termina i processi `ollama`.
- **OllamaSharp è l'interlocutore** per tutto ciò che espone.
  - Pagine e servizi usano direttamente `IOllamaApiClient` e i suoi tipi (`Model`, `RunningModel`, `PullModelResponse`, `ShowModelResponse`).
  - Niente wrapper che li ricopiano e niente chiamate HTTP dirette a `/api/*`.
  - Ciò che manca si aggiunge come extension method su `IOllamaApiClient` (`Ollama/OllamaClientExtensions.cs`), nello stile di `OllamaApiClientExtensions`.
  - OllamaSharp 5.5.0 non ha alcuna astrazione per il catalogo, quindi catalogo e controllo del processo sono codice nostro.
- **Faceted browsing** (`Catalog/CatalogFacets.cs`):
  - le faccette sono in AND tra loro, le opzioni della stessa faccetta in OR;
  - i conteggi sono disgiuntivi: ogni opzione conta applicando le altre faccette.

## Note tecniche

- **Catalogo ollama.com, senza API pubblica:**
  - `/search?page=N` richiede l'header `HX-Request: true`, altrimenti restituisce sempre la prima pagina;
  - le dimensioni dei tag sono solo in `/library/<nome>/tags`;
  - se il markup cambia, si corregge solo `Catalog/OllamaCatalogScraper.cs`;
  - le richieste sono limitate da `Catalog:MaxConcurrentRequests` e la cache sta in `Ollamazing.Web/data/` (non committare).
- **Stato condiviso:** `CatalogService`, `PullManager` e `ServerStatusMonitor` sono singleton che notificano con eventi.
  - I componenti ereditano `LiveComponentBase` e si iscrivono con `Watch(...)`.
  - `ServerStatusMonitor.Current` contiene modelli installati e residenti: è l'unica fonte per le pagine. Dopo un'operazione si chiama `ProbeNowAsync()`.
- **Razor:** niente stringhe tra doppi apici dentro `@onclick="..."`; si sposta la lambda in un metodo.
- **Lingue:** UI in inglese; commenti nel codice in inglese, secondo la skill `code-commentation`.
- **Shell:** in questo ambiente `rm` è un alias interattivo, quindi si usa `/bin/rm -f`.
