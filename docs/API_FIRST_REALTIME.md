# Live Laserfiche data and event-driven content indexing

## What changed

The existing .NET 8 application remains the web host. It can also run as the Windows service `LaserficheReportsIndexer` (display name **Laserfiche Reports AI Indexer**). The current blue/white interface, cards, reports and local AI services remain in place.

| Request | Source |
| --- | --- |
| Document/folder counts, current documents, field values, definitions, templates, dates, extensions | Current user's Laserfiche REST API session |
| Exact field equality | Live field definitions followed by streaming live entries/field values; no vector search |
| Summary/analysis with a field, template, folder, explicit IDs or “all documents” | Live API filter first; every matching document's available content chunks, processed in bounded batches |
| Open-ended semantic question | Repository-scoped lexical/vector retrieval; live existence/permission/modification checks; explicitly non-exhaustive |
| Listener status, pending work, index coverage | Local operational SQLite state, not a repository metadata source |

`QueryRouter` uses deterministic supported intents. It asks for clarification when a condition is ambiguous or unsupported instead of silently turning a metadata question into semantic search. Date queries use `Reports:TimeZone` (default `Asia/Riyadh`); “recently modified” means the last seven days. Native field search syntax is not guessed: the current exact field tool enumerates the API and tests live values. This is correct without Top-K but can be slower on a large repository. Traversal uses a temporary SQLite folder/seen-ID spool and API continuation pages instead of retaining the whole repository in memory. Counts continue beyond the 500-row display bound; the complete result can be downloaded. API calls are not a transactionally consistent repository snapshot; concurrent changes during enumeration are disclosed.

The document list now comes from the API and remains usable when the vector database is down. Old cached analytics are no longer injected as the primary analytics service. Images are counted only when returned page metadata positively identifies an image. Unknown page types produce an explicit partial image count; page count is not presented as image count. Electronic-document presence uses the API's `isElectronicDocument`, not a filename/size guess.

## Notifications: official SDK and actual runtime validation

There is **no RepositoryAccess SDK assembly in this repository or the development environment**. The web app has REST adapters, not a native SDK reference. RepositoryAccess targets .NET Framework, so `integrations/laserfiche-notifications/listener.ps1` runs separately under Windows PowerShell 5.1. The host remains .NET 8.

The bridge loads the **installed** `Laserfiche.RepositoryAccess.dll`, checks the required types and notification enum values, logs its actual assembly version, authenticates, connects `NotificationManager`, subscribes and waits for real repository notifications. It uses official methods documented here:

- [Signing in](https://developer.laserfiche.com/docs/repository-access-guides/getting-started-with-the-sdk/signing-in-to-a-repository/)
- [Notifications](https://developer.laserfiche.com/docs/repository-access-guides/sdk-tutorials/notifications/subscribing-to-notifications-and-retrieving-notifications/)
- [Activity log recovery](https://developer.laserfiche.com/docs/repository-access-guides/sdk-tutorials/notifications/reading-the-activity-log/)

Missing/ambiguous assemblies, unsupported subscriptions, licensing, login or connection errors show **Failed**, never **Connected**. No timer silently replaces the listener. The SDK path can be supplied in settings; automatic discovery succeeds only when there is exactly one installed candidate. The native repository server name can differ from the REST/API server hostname: set `Realtime:RepositoryServer` accordingly. All repositories in this application currently share the configured API server and SDK server.

The bridge subscribes before reading the activity log. On startup and after a notification it reads the ordered activity range after the durable cursor. A JSON event is acknowledged on stdin only after the backend commits its work item and cursor together. Notification heartbeats keep the host aware of bridge failure; they do not query/index the repository. The activity log is bounded: a rollover or sequence reset queues **metadata reconciliation**, not unconditional OCR of the entire repository. Unknown activity-to-entry mapping also queues reconciliation safely. A release activity explicitly containing only rename/move/field/template changes updates metadata; unknown/content/page activities rebuild affected content conservatively.

## Durable work and index updates

Operational state is `%ProgramData%\LaserficheReports\realtime\state.db` (WAL SQLite). It contains repository cursors, pending work, retry/dead-letter state and a document manifest. Keys include API server and repository; each work key additionally includes Entry ID. Changes for one entry coalesce to the highest required operation, with a 750 ms debounce. A version check prevents completion of an old operation from deleting a newer event. One document consumer and one metadata-reconciliation consumer per repository process their respective queues, so an initial traversal does not block live document updates; an exclusive instance lock prevents two hosts consuming the same state directory.

A first repository connection queues initial metadata traversal automatically. Its completion marker also handles an empty repository correctly. Enumeration creates document work only for new, changed or incomplete entries. Folder path changes and activity-log gaps use metadata reconciliation; unchanged indexed documents are not sent through OCR again. Interrupted traversal/work is retained and resumes via retry after restart. Failure retries use exponential backoff up to five minutes, with eight attempts by default. Exhausted operations remain visible and can be retried by an administrator.

Every operation rereads current Laserfiche state. A deleted document (404) removes only that repository/Entry ID's `laserfiche-reports` metadata/chunk records. A restore indexes its current state. A permission failure is not treated as deletion. Missing manifest entries are individually checked before removal, and failed folder enumeration never triggers bulk deletion. Live permission/existence checks prevent ordinary RAG from exposing removed or inaccessible documents before cleanup finishes.

Metadata-only changes update parent/chunk names, paths and modification timestamps while preserving page vectors; metadata representations alone are regenerated. Content changes extract the latest available page text/OCR, calculate a SHA-256 content hash and index version, and reuse unchanged content when the pipeline matches. Changed content replaces only that entry's chunks transactionally. PostgreSQL advisory transaction locks serialize same-entry writes. Incomplete extraction preserves the previous index and retries; retrieval rejects an index with a modification timestamp different from the current API state. Hash reuse and forced administrative rebuild are separate paths.

Electronic files that Laserfiche exposes as searchable/renderable pages use the existing text/OCR pipeline. A binary electronic file without accessible page text/rendering is **not claimed to have been fully analyzed**: its metadata remains live, and the content report records missing coverage. No new external OCR/AI service is introduced. Ollama, PaddleOCR and LangGraph still run locally and require their existing setup/start procedures.

## Installation / another Windows computer

No source changes are required. Install the Laserfiche SDK/native client dependencies compatible with your Repository Server, and ensure the service account can authenticate, read the intended repository and read/subscribe to its activity log. Configure existing API/AI/PostgreSQL settings plus:

```json
{
  "Laserfiche": {
    "ServerUrl": "https://api-server",
    "RepositoryId": "RepositoryName",
    "ApiVersion": "Auto"
  },
  "Reports": { "TimeZone": "Asia/Riyadh" },
  "Realtime": {
    "Enabled": true,
    "SdkAssemblyPath": "C:\\InstalledSdk\\Laserfiche.RepositoryAccess.dll",
    "RepositoryServer": "repository-server",
    "SecureSdkConnection": true,
    "Repositories": ["RepositoryName"],
    "AdminUsernames": ["your-laserfiche-admin"],
    "DebounceMilliseconds": 750,
    "MaxAttempts": 8
  }
}
```

Keep server TLS certificate trust configured normally. The bridge does not disable certificate validation. The SDK and service identity must have the required Laserfiche licensing. The example SDK path is a placeholder: use the installed assembly path, not a downloaded imitation. `StateDirectory` is optional and defaults to ProgramData; if overridden, use the same value when protecting credentials and give the service identity access to it.

Run from an elevated PowerShell in the checked-out project (the installation build needs .NET 8 SDK):

```powershell
.\scripts\install-indexer-service.ps1
.\scripts\protect-indexer-credentials.ps1 -Repository 'RepositoryName'
# Enter the dedicated Laserfiche service account in the protected credential prompt.
Start-Service LaserficheReportsIndexer
Get-Service LaserficheReportsIndexer
```

The service uses LocalService by default. The credential script encrypts with machine DPAPI and restricts ACLs to SYSTEM, administrators and LocalService. Credentials never appear in service command arguments or application settings. They are passed to the bridge through its private process environment. Browser logins do not become service credentials. Re-create credentials on the target machine; encrypted credentials are machine-bound. Existing environment/secure-store credentials remain a supported fallback. Use a dedicated service account whose repository permissions are appropriate; every interactive answer still uses the logged-in user's live access checks.

The installed self-contained executable does not need a separate .NET runtime. Existing LangGraph/Ollama/OCR processes are not installed or started by this script; run their established local service/start procedures. The notification/indexing worker runs with the web host, independently of any browser. Hosting remains loopback-only; remote browser access is not enabled by this change.

For a normal development launch, configure the same settings and run the existing web project. To keep an existing IIS deployment, do not run two enabled indexers against one state directory. Set `Realtime:Enabled=false` on the IIS host, run the Windows service as the sole worker on a different loopback port if needed, and point both hosts to the same secured state directory and vector store. SQLite status is shared; only the service consumes events. The install script refuses to overwrite an existing service and does not edit IIS/Web Client bindings. For upgrades, stop the service, publish updated files, and start it; retain the state directory and encrypted credentials.

After changing an embedding/chunk/OCR pipeline or repairing index corruption, a username explicitly listed in `AdminUsernames` can open advanced options and confirm **إعادة بناء الفهرس**. **مزامنة الآن** is manual recovery only. Both queue backend work; neither depends on the browser remaining open. Normal indexing no longer uses browser traversal/localStorage checkpoints. The periodic UI status refresh reads operational status only; it never submits index jobs or polls repository changes.

## Database safety and retention

No production table/column is dropped, and no global document cleanup is performed. `public.documents` remains the existing content/vector store. New `content_hash`, `metadata_hash`, `index_version` and modification fields are JSON metadata additions, so no PostgreSQL schema migration is required. Operational SQLite tables are created additively; the initialized marker uses an additive migration if missing. Legacy index rows without a verifiable timestamp are not treated as fresh semantic evidence; the automatic first sync/rebuild refreshes them.

Generated full reports are owned by repository and browser session, expire after one hour, and are downloaded only through authenticated endpoints. They are local temporary reports, not a replacement repository database. Keep the state/report directory secured. Back up durable operational state using a SQLite-consistent backup (including WAL when necessary), not a raw copy while a writer is active. Persistent queue/cursor state is important for catch-up after restarts.

## Verification and honest limits

Automated verification covers routing, live counts independent of PostgreSQL/embeddings, matching beyond the display bound, continuation duplicate handling, background repository flow isolation, SHA-256/metadata comparison, durable checkpoint/restart, coalescing/new-event races, failed work retention, repository-scoped deletion state, existing PostgreSQL/pgvector retrieval, UI behavior and PowerShell syntax. CI builds/tests the full solution on Windows and starts the actual web host for UI/status/authentication smoke tests.

This environment cannot create/change/delete/restore documents on your Laserfiche server or inspect your installed SDK/version. Runtime type/subscription validation is implemented; an actual SDK notification session, Windows SCM installation and the complete server event test matrix remain to be executed on the target Windows machine. Check the UI listener state and Windows Application Event Log for failures. A successful build or web smoke test is not evidence that a real Laserfiche notification connection has succeeded.

Acceptance checks on the target machine: create/import/scan a document; change a field; alter a page/electronic file; rename/move; delete/restore; stop the service, change entries and restart; disconnect the vector store and ask a live count; repeat an event/import a batch; switch repositories and verify no prior-repository report/content appears. Confirm only the affected document's vectors change for content edits, page vectors remain unchanged for metadata-only edits, and recovery consumes queued/cursor work.
