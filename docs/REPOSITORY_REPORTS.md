# Repository-specific reports and downloads

## Select a repository

The login form accepts the Laserfiche repository ID and the user's repository
credentials. The configured `Laserfiche:RepositoryId` supplies the initial value.
Users can type another repository ID on the configured API server, or use
**اكتشاف المستودعات** after entering credentials for a known repository. Discovery
uses the live API; logging into the chosen repository verifies access separately.
No machine-wide repository configuration is changed by a login.

The top bar displays the active repository. **تغيير المستودع** opens the login
form. Finish the current question or pause ingestion before changing it. An account
may have a different password or permissions in another repository.

History and ingestion checkpoints are partitioned by server, repository and user.
Old history without repository identity is retained in browser storage but is not
loaded into the new history. Clear sensitive browser history on a shared computer.
The server's existing index filters continue to use the active repository and
project, and every retrieved document is checked against the live account.

Requests send the expected repository and login generation. A stale tab receives
HTTP 409 after another tab changes the account or repository. Requests sharing a
browser session are serialized and committed before releasing their session gate,
so a login cannot race an in-flight report or ingestion. A long operation may make
other operations in the same browser wait. The browser rejects late responses
from a previous session and pauses an ingestion scan on a stale-session response.

## Download each report

Every completed answer has its own download controls, and each result table also
has a **تنزيل هذا الجدول** control. A table export includes the original report
question/scope and source appendix. Empty-result reports can
also be downloaded. They preserve the answer, question, scope, generation time,
review status and original source text.

- **Word (.docx):** a real Open XML package with RTL paragraphs, landscape tables
  and repeated table headers; generated locally without an external converter.
- **Excel (.xlsx):** report details, a separate sheet for each result table and a
  sources sheet. Cells are literal text, including leading `=`, never formulas.
  Long cell contents are split into continuation rows to avoid Excel's text limit.
- **PDF (حفظ عبر الطباعة):** opens a self-contained report and the browser's print
  dialog. Choose **Save as PDF / Microsoft Print to PDF**. This is browser-assisted
  PDF saving, not a direct PDF download. Allow the report popup if blocked.
- HTML and Markdown remain available.

The Office exporters use standards-based ZIP/XML packages and ship with the app;
no internet, additional Office installation or new production database is needed.
They do not execute document HTML, scripts, formulas or hyperlinks.

## Open the report's documents in Laserfiche

The button uses explicit document IDs returned with the report. For live inventory
reports these are all listed documents; for AI reports they are the documents used
by the selected evidence rows. It does not include unused retrieval candidates.
Older saved reports without explicit IDs have this button disabled.

The server verifies the report's repository and rechecks document access before
returning Web Client search URLs. IDs are combined with `|` using `{LF:ID=...}` and
encoded into the same `Browse.aspx?db=...#?search=...` link shape used by
the Dashboard. Reports containing over 500 distinct IDs are split into groups,
all of which are displayed for the user to open. No password is put in a URL.
Use the same hostname as your existing signed-in Web Client. A login on a machine name does not share cookies with localhost; using localhost can create a second session and hit license limits. The app reuses a report results window.

The default Web Client directory is `/laserfiche` on the configured server's origin.
For another virtual directory or host, set its actual directory URL in local settings:

```json
{
  "Laserfiche": {
    "WebClientBaseUrl": "https://localhost/laserfiche"
  }
}
```

Use the directory URL, without `Browse.aspx`, query parameters or a fragment, and
preserve the directory spelling/case used by the installed Web Client.

## Validation boundaries

Regression tests cover history isolation, late responses, export safety, URL
encoding and grouping. Office packages are inspected using DOCX/XLSX readers.
Live repository discovery, installed Web Client navigation and model quality still
require testing on the machine with Laserfiche and Ollama. A passing mocked test
is not a measurement of actual model accuracy on the user's documents.

## AI planning and multiple reports

Every question is analyzed by Ollama with the live field/template catalog. Validated tool plans execute against the repository; there is no keyword question router. Independent requests produce independent report cards and downloads. Latest-created and latest-modified each request one row with their own server-side ordering. Tables include both timestamps, and missing search-result paths are resolved from the current entry and parent folders.

The model summarizes retrieved metadata, and a second model call checks its claims against exact evidence. Unverified summaries are excluded; retrieval remains the authority for tables and counts. Restart both services after updating. Graph health must include `routingVersion: ai-multi-report-v3`. Local model accuracy and Windows Web Client session behavior require validation on the installed machine.

## Local model timeouts and startup checks

Every question checks the running graph protocol and Ollama model registry before planning. Health distinguishes a stopped Ollama from a missing model. The browser receives distinct causes for graph version mismatch, model timeout, invalid AI output and model busy. Schema-invalid plans get one reanalysis attempt; neither attempt executes an invalid plan.

Defaults favor completing verified reports: 600 seconds per Ollama call, 1,500 seconds per graph HTTP request and 3,600 seconds for the complete chat request. Laserfiche asynchronous search polling allows 300 seconds and the whole search 600 seconds. These are deadlines, not mandatory waiting periods. Report planning/presentation still uses live metadata and evidence checks; a longer timeout does not establish model accuracy.

After updating, stop the old graph and web processes with Ctrl+C and start both again:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\reports-graph\start.ps1 -Model qwen2.5:7b -TimeoutSeconds 600
```

In another terminal, run the web project. Verify the graph health in a third terminal:

```powershell
Invoke-RestMethod http://127.0.0.1:8766/health
```

It should report `status: ready`, `model: qwen2.5:7b`, `modelTimeoutSeconds: 600` and `routingVersion: ai-multi-report-v3`. A 503 body specifies whether Ollama is stopped or the model is absent. `ReportsGraph:TimeoutSeconds` and `Reports:RequestTimeoutSeconds` are configurable in local settings. Keep the graph HTTP timeout above the combined model calls and the total chat timeout above its stages. Restarting is required because the model client and HTTP client settings are created at startup.
