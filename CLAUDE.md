# CLAUDE.md

Guida operativa per Claude Code su questo repository.

## Cos'è Ollamazing

Dashboard Blazor Server (interattività server via SignalR, .NET 10) che fa da frontend a un'installazione Ollama:

- **Catalogo** dei modelli ufficiali di ollama.com, con navigazione a faccette: classe locale/cloud, prezzo gratuito/a pagamento, capability, dimensione con slider min/max.
- **Download** (pull) dei modelli in background, con avanzamento in tempo reale.
- **Server**: stato di Ollama, avvio/arresto/riavvio, modelli caricati in memoria, log.
- **Modelli locali**: aggiorna (re-pull), elimina, scalda (carica in memoria), scarica dalla memoria, dettagli.
- **Account**: pairing del server con l'account ollama.com, crediti, consumi per modello, sign-out.

## Struttura

```
Ollamazing.slnx              solution (formato slnx), niente cartella src/
Ollamazing.Server/           class library: tutto ciò che parla con Ollama locale, senza UI né ASP.NET
  Configuration/            opzioni delle sezioni Ollama e ServerControl di appsettings.json
  Lifecycle/                controllo del processo `ollama serve` (start/stop/restart, auto-start)
  Status/                   ServerStatusMonitor, stato condiviso del server
  Account/                  AccountMonitor e modelli di /api/me, /api/balance, /api/usage
  Pulls/                    PullManager, download in background
  Extensions/               extension method su IOllamaApiClient e AddOllamazingServer(...)
Ollamazing.Web/              web app Blazor: solo UI e catalogo
  Catalog/                  scraping HTML di ollama.com (AngleSharp), cache su disco, motore a faccette, CatalogOptions
  Components/               UI Razor: Layout, Pages (*Page.razor), Shared
```

- Web dipende da Server, mai il contrario. Server dipende solo dalle astrazioni `Microsoft.Extensions.*` e da OllamaSharp.
- Il namespace segue la cartella (`Ollamazing.Server.Status`, ...). Niente cartella `Process/`: il namespace oscurerebbe `System.Diagnostics.Process`.
- I servizi del server si registrano solo con `AddOllamazingServer(configuration)`; `Program.cs` registra solo ciò che è della web app.

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
  - Ciò che manca si aggiunge come extension method su `IOllamaApiClient` (`Ollamazing.Server/Extensions/OllamaClientExtensions.cs`), nello stile di `OllamaApiClientExtensions`.
  - OllamaSharp 5.5.0 non ha alcuna astrazione per il catalogo, quindi catalogo e controllo del processo sono codice nostro.
- **Faceted browsing** (`Ollamazing.Web/Catalog/CatalogFacets.cs`):
  - le faccette sono in AND tra loro, le opzioni della stessa faccetta in OR;
  - i conteggi sono disgiuntivi: ogni opzione conta applicando le altre faccette.

## Note tecniche

- **Catalogo ollama.com, senza API pubblica:**
  - `/search?page=N` richiede l'header `HX-Request: true`, altrimenti restituisce sempre la prima pagina;
  - le dimensioni dei tag sono solo in `/library/<nome>/tags`;
  - le tariffe cloud per MTok sono solo in `/pricing`, sezione `#model-pricing`: righe collegate a `/library/<nome>`, con variante opzionale (`gpt-oss:120b`) e suffisso `(Off-Peak)`;
  - se il markup cambia, si corregge solo `Ollamazing.Web/Catalog/OllamaCatalogScraper.cs`;
  - le richieste sono limitate da `Catalog:MaxConcurrentRequests` e la cache sta in `Ollamazing.Web/data/` (non committare).
- **Account ollama.com:** l'account è legato alla chiave ed25519 del server (`~/.ollama`), non a Ollamazing.
  - Endpoint del server locale (non coperti da OllamaSharp): `POST /api/me` (profilo, oppure 401 con `signin_url`), `POST /api/signout`, `GET /api/balance`, `GET /api/usage?range=24h|7d|30d&group_by=model`.
  - `POST /api/signout` revoca la chiave anche su ollama.com (verificato): il dispositivo sparisce dall'account e il nuovo login rifà il pairing.
  - Il pairing è l'handshake di `ollama signin`: si apre `signin_url` (solo se `https://ollama.com`), l'utente approva su ollama.com, la pagina interroga `/api/me` finché non risulta collegato. Mai credenziali o chiave privata in Ollamazing.
  - Le extension usano un `HttpClient` proprio sull'`Uri` del client, perché OllamaSharp non espone il suo.
- **Stato condiviso:** `CatalogService`, `PullManager`, `ServerStatusMonitor` e `AccountMonitor` sono singleton che notificano con eventi.
  - I componenti ereditano `LiveComponentBase` e si iscrivono con `Watch(...)`.
  - `ServerStatusMonitor.Current` contiene modelli installati e residenti: è l'unica fonte per le pagine. Dopo un'operazione si chiama `ProbeNowAsync()`.
- **Razor:** niente stringhe tra doppi apici dentro `@onclick="..."`; si sposta la lambda in un metodo.
- **Lingue:** UI in inglese; commenti nel codice in inglese, secondo la skill `code-commentation`.
- **Shell:** in questo ambiente `rm` è un alias interattivo, quindi si usa `/bin/rm -f`.
