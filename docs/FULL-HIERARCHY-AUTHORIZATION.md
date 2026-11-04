# Full hierarchy authorization — implementation and release acceptance

Implementation base: `d824254` on `fix/typed-semantic-repository-plans`, which includes
PR #46 (`72fb61f`) and its session isolation fixes. Work branch:
`feat/full-hierarchy-authorization`. This change is for review; live Laserfiche and
Windows installer acceptance below remain required. It does not change repository ACLs.

## Original cause and trust boundary

The existing bridge is a targeted, opt-in diagnostic. Its explicit trustee/entry disclosure
allowlists deliberately prevent it from checking every normal search result. A successful
current-user SearchAsync/GetEntry read was previously sufficient for normal results. That
matches entry visibility but cannot establish this application's stricter ancestor policy,
especially when inheritance is disabled on an entry. The actual permissions of document 553
and its ancestors cannot be established from ACL screenshots or this implementation environment.

The policy now requires Browse **and** Read on the entry and on **every ancestor including
the authoritative repository root**, plus successful reads with the interactive user's API
session. There are no user/entry allowlists or administrator exceptions in enforcement.
Diagnostic disclosure retains its separate allowlists and remains disabled by default.
Unknown verification is not absence or a permission denial: it aborts the protected operation.

## Architecture and algorithm

* `HierarchyAuthorizationHandler` is installed on the production `LaserficheAuthenticated`
  transport, including bearer-session retries. It obtains the gateway from the **current HTTP
  request**, not the HttpClientFactory handler scope. Host/repository mismatches are refused.
  Repository discovery contains connection metadata; entry results are separately guarded.
* `FullHierarchyAuthorizationService` checks completed Reports/LFDS sign-in, active repository,
  exact API/native binding and server principal. It invokes the existing private process bridge
  in full-hierarchy mode. There is no public privileged verification endpoint.
* The SDK resolves password-account SID using a fresh secure user login, or uses the existing
  exact authenticated issuer/subject-to-SID mapping for SSO. Display names never select trustees.
  Scope hashes now include server URL, repository ID, session ID/generation, and subject.
* The service SDK session only supplies structural IDs and `Entry.GetEffectiveRights(id,
  AccountReference, ISession)` evidence. The actual pinned SDK 12.0.2608.7 assembly/XML was restored
  and the net48 bridge compiled. `EntryInfo.ParentId` and that rights overload are real APIs.
  No Cloud `/Rights` or `/AccessControl` endpoint is introduced.
* Root is resolved by SDK `Entry.GetEntryInfo("\\", service)`. Follow ParentId from the requested
  entry until that exact root ID; check every level independently. A visited set, positive IDs,
  folder-type validation and a 256-node cap reject cycles, missing roots and incomplete chains.
  Re-read IDs/parents/effective rights before accepting the chain; a detected move/revocation is
  unavailable. Neither a path string, parent sentinel nor reaching the depth limit proves root.
* Only a nonce/scope/repository-bound minimal decision and the verified ID chain cross the
  private pipes. Chain IDs remain internal. The Web gateway validates each ID/ParentId again
  through the **interactive token**, including all ancestors. API 401/403/404/timeouts are not
  upgraded to success. Principal/repository scope is checked again before returning.
* Requested fields, page text/images, exports and electronic files still use the user's API
  operation. SDK effective rights do not replace content/field/tag/volume enforcement by LF.

There is no positive authorization cache or long-lived shared user SDK session. Service
credentials remain DPAPI CurrentUser-protected as in the original bridge, outside source,
arguments, logs and browser output. No ACL, group, inheritance, restore or purge write is added.

## Search, counts, folders and AI

Search/children responses are candidate lists. Each distinct candidate passes the gateway before
mapping, aggregation, reports, planning choices or model evidence. Denied rows are removed in the
backend; unknown decisions invalidate the operation. Candidate count properties are removed;
raw counts cannot become visible authorized totals. Missing candidate rows without continuation
fail closed. Continuations survive even when an entire page is filtered out.

Production search reads from offset zero through all continuations, de-duplicates allowed rows,
then computes authorized totals and slices the requested page. It does not apply a candidate
server offset to an already-filtered set. Complete reads still retain their existing 10,000-row
safety limit; there is also a 10,000-verification request budget. Narrow queries that exceed the
budget rather than displaying an incomplete exact count. Verification uses at most four SDK
processes concurrently; each has a separate 2–30 second budget and the existing child watchdog.
Folder tree traversal receives only authorized children, so it cannot recurse through a blocked
ancestor. Direct IDs and inherited-rights-off entries take the same route.

The 15-minute analytics data cache is removed from production registration; statistics are live.
Existing cache implementation/tests remain for compatibility, but it is not the production source.

OCR refresh and retrieval now share a unique **request** authorization scope. Production ingestion
cannot reuse unchanged/previous OCR chunks or preserve an old index as a substitute for a fresh
content read. New chunks carry `authorization_scope`; both direct and hybrid retrieval add the
same SQL scope predicate before ranking. Another request overwriting shared chunk storage can
cause missing results, but cannot supply its chunks to this request. No database migration is
needed; older unscoped chunks are ineligible. Current hierarchy, fields and actual page/electronic
file access are checked again before sending evidence to the answer model. Historical assistant
report bodies are excluded from planner context; user turns and clarification context remain.

## Saved results, exports and operation dependencies

Chat responses receive an opaque 30-minute authorization receipt. A dedicated 16 MiB bounded
memory store contains only scope/dependency evidence, not report bodies. Scope includes server,
repository and the server-authenticated principal generation. Eviction, restart or expiry requires
rerunning the question. A random browser-supplied list of IDs cannot mint a receipt.

After model execution, all authorized entry dependencies are checked again. Successful field,
detail, text/page and file operations are recorded. Metadata/small-content response fingerprints
must still match on revalidation; changed/restricted fields invalidate the snapshot. Large or
unknown-length electronic-file/export streams remain streamed; their operation permission is
rechecked instead of buffering the complete file. Projected search fields add a real current-user
field-read dependency. Receipts re-run these guarded operations when restoring a saved answer,
copying a report or exporting Word/Excel/PDF/HTML/Markdown. Open-in-Laserfiche retains its existing
live guarded search check. Old saved messages with no receipt are not restored as verified reports;
saved scan inventories are not restored. Multi-report sections share the enclosing receipt.

The browser already possesses content delivered while access was allowed. This does not provide
DRM or revoke downloaded files, screenshots, localStorage bytes or another person's copies.
Client export controls recheck permission; they cannot prohibit a user from copying bytes already
received. Export authorization refers to the report snapshot, not a promise that the query's
membership/count is unchanged after new entries are added. Rerun the question for a new snapshot.

## Recycle Bin and historical audit data

Recycle Bin retains its separate native, secure **interactive-user SDK** listing policy. Deleted
entries are not resolved as live IDs and historical paths never prove a current ancestor chain.
Server-generated recycle snapshots are fingerprinted and relisted for receipt revalidation.
Web Client-origin recycle reports are reread through the existing origin/session-bound native
bridge before restoration/export, and changed snapshots require rerunning the question.

Live-entry Audit Trail events now require the hierarchy gateway before use. An event without a
live, verifiable EntryId makes that audit report unavailable. Historical/deleted IDs that cannot
be checked also fail closed. This intentionally limits legacy audit outputs; use the existing
Recycle Bin feature for current deleted entries. No historical ancestor permission is invented.

## Deployment (installed service, offline runtime)

1. Build/publish on **Windows x64** through the existing `build/publish.ps1 -Version <new-version>`
   or transfer-package workflow. The Web MSBuild targets already include the rebuilt net48 bridge,
   pinned SDK/dependencies/EULA; this policy document is added to its published payload.
2. Install/upgrade Reports using that new EXE/MSI, or update the published installation using the
   existing source/transfer deployment process. Visual Studio and `dotnet run` are unnecessary.
   A source-only pull does not update an already-installed service's binaries.
3. Merge a repository binding into the administrator-owned runtime/local configuration loaded by
   Reports. Preserve unrelated options. The API repository ID and native repository name may differ.

```json
{
  "PermissionVerification": {
    "Enabled": false,
    "TimeoutSeconds": 15,
    "Repositories": {
      "YOUR_API_REPOSITORY_ID": {
        "ApiServerUrl": "https://YOUR_API_SERVER",
        "ServerName": "YOUR_NATIVE_LASERFICHE_SERVER",
        "RepositoryName": "YOUR_NATIVE_REPOSITORY_NAME",
        "ServiceCredentialFile": "",
        "Subjects": []
      }
    }
  }
}
```

`Enabled` controls the old **diagnostic** only; full hierarchy enforcement always runs. With no
binding/bridge/identity evidence, protected reads are unavailable. Do not solve a deployment
failure by removing the handler, marker registration, gateway or content scope. There is no
production configuration flag that falls back to unchecked normal search.

4. Use a dedicated repository-linked Windows service identity for SDK security inspection, or
   the existing `protect-permission-service-credential.ps1` under the actual service owner to
   create a DPAPI credential. Do not use that identity for user report/content operations.
5. For LFDS SSO, configure exactly one authoritative `Subjects` issuer/sub-to-trustee-SID mapping
   per native repository. Opaque tokens or missing/duplicate mappings are **unavailable**. Local
   and LFDS password login require native SDK acceptance of that exact interactive account.
6. Restart Reports and run `test-permission-bridge.ps1 -WebPath <installed-web-path>` for payload,
   x64 and net48 checks. `/health` reports missing bindings/payload as unhealthy, but is a deployment
   check, not proof of SDK licensing/connectivity or LFDS security correctness.
7. Execute the real acceptance matrix below before production rollout. Offline runtime makes no
   new Internet calls; build/package restore still needs staged/cached NuGet dependencies.
   Existing uninstall safety remains: no LF component, repository or external credential deletion.

## Evidence and required live acceptance

Automated validation results are recorded below after the final checks. The new pure-policy tests
exercise the exact source compiled into the SDK bridge; transport tests exercise backend filtering.
Neither category establishes actual LF rights, group membership, SID mappings, licensing or tags.

| Scenario | Automated coverage | Live LF result |
| --- | --- | --- |
| Document 553 allowed, every ancestor including root allowed | Exact chain/ID test | Not executed |
| 553 allowed but parent 18 / any higher ancestor denied | Denial at four ancestor levels | Not executed |
| Entry itself denied / parent is a document / broken ID / cycle | Fail-closed tests | Not executed |
| Root-only folder / document directly under root / depth cap | Policy tests | Not executed |
| Move or rights revocation during chain verification | Second-pass policy tests | Not executed |
| User A versus B / cross-repository and cross-host replay | Policy, transport and receipt tests | Not executed |
| Filtered empty page followed by allowed entries | Search continuation/paging test | Not executed |
| Hidden row names, field values and raw totals | Transport disclosure tests | Not executed |
| Fields changed / real content 403 / revoked export | Dependency/receipt/UI tests | Not executed |
| Entries 475, 489, 490 | Synthetic pagination IDs only, no assumed rights | Not executed |
| LFDS SSO/password, Windows/AD groups, explicit Deny, security tags | Existing identity/protocol tests only | Required |
| Field/template/volume security, RM restrictions, redactions | Actual user API remains mandatory | Required |
| Installed Windows service, clean EXE/MSI install/repair/upgrade/uninstall | Existing CI workflow retained | Required |
| Offline native SDK/licensing/prerequisites | No new runtime remote dependency | Required |

For live 553 acceptance: log in as amjd, record the real API token identity, SDK SID, resolved IDs,
effective Browse/Read and actual folder reads through to root. If any required ancestor is denied,
553 must be absent from all search/count/folder/AI/OCR/export/link outputs. Repeat for ADMIN and
another ordinary account without bypass exceptions. Record build SHA, timestamp, operation,
expected/actual status and trace ID without names/paths/credentials in public diagnostics.

No live repository credentials/server or Windows host are available here. There is **no** claim
that document 553, LFDS/AD/tag behavior, installation, or production latency passed. Native API
security/session caching and revocation timing must be measured. Independent checks cannot create
an atomic repository transaction: permission/content can change after the last check. Large
stream replay proves current operation access, not byte equality of a cached answer. Test
redactions/content revocation explicitly before accepting cached/export behavior for that scope.

Performance tradeoff: this version bounds work and concurrency but deliberately does not cache
positive folder decisions or batch SDK sessions across candidates. It can make broad searches
substantially slower. Shared-ancestor batching and large-repository latency remain review/target
benchmark work; no fabricated timing or scale result is supplied.

## Local validation record — 2026-11-04

* .NET SDK **8.0.427**: Web/Application/Infrastructure/Domain and both test projects compile.
  Existing Windows drawing/platform and ASP.NET analyzer warnings were observed; no compile errors.
* Pinned **net48 x64 SDK bridge** restore/build: passes, including the real ParentId/effective-rights API.
* Web xUnit suite: **269 passed, 1 skipped** (external PostgreSQL/pgvector integration).
* Infrastructure xUnit suite: **317 passed**, including authorized paging over an empty filtered page.
* UI suite: **29 passed**, including server revalidation refusing revoked report downloads.
* Python planner suite: **285 passed, 1 skipped** (external converter case); no planner source changes.
* Total executed cases: **900 passed, 2 skipped**. `git diff --check` passes.

VSTest failed in this execution host's process/diagnostics environment. Both .NET suites were
therefore discovered and executed by the **real xUnit framework** with parallelism disabled in a
temporary in-process runner, as with the earlier permission-bridge implementation. The temporary
runner is not shipped or added to product source. These results are not native Windows runs.
Initial Python discovery required installing the declared test dependencies and removing this
host's inherited SOCKS proxy variables for the local test run; the final full run above passed.
No existing test was removed/relaxed; existing UI download tests now await the required live check.
Windows CI, native installer acceptance and the live matrix remain outstanding release gates.

### Changed source responsibilities

Added: application `IFullHierarchyPolicy` / `IContentAuthorizationScope`; Web
`FullHierarchyAuthorizationService`, `HierarchyAuthorizationHandler`, `ProtectedReadTracker`,
`ReportAuthorizationReceipts`, `ContentAuthorizationScope`; SDK-independent
`tools/permission-bridge/HierarchyAuthorization.cs`; new hierarchy/transport/receipt tests; this document.

Updated: permission contracts/principal resolver/process adapter/deployment health; Web DI/endpoints,
chat/OCR SQL, client saved history/export flows, Audit Trail source; infrastructure search, analytics
registration and ingestion; Web publish/test project definitions; original permission documentation;
existing search paging/UI test suites. SDK/program changes reuse the existing bridge and packaging.
Native installer build/install scripts themselves are unchanged; the modified published payload is
picked up by their existing recursive inclusion. No installer binary was built/published locally.
