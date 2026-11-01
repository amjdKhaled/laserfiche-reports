# Targeted permission verification (Windows / On-Prem)

Status: review implementation, disabled by default. A successful build is not proof of LFDS/AD
trustee correctness, security-tag behavior, complete visibility, licensing, or successful installation.
Live acceptance on Laserfiche 12 is a release prerequisite; no live repository is available in the
implementation environment. Do not enable broad disclosure as a substitute for those checks.

## Data flow and trust boundaries

Normal search, fields, document streams, folder contents, counts and OCR continue through the
signed-in user's Repository API session. The permission bridge is a **read-only diagnostic** fallback
for a precise entry lookup. It never supplies ordinary search results, file content, OCR snippets,
paths, names, source links, ACLs or hidden-match counts. General zero-result queries never use it.

The backend uses a fixed executable at `permission-bridge/LaserficheReports.PermissionBridge.exe`.
This x64 net48 child uses RepositoryAccess **12.0.2608.7**, the same pinned version as the existing
recycle-bin reader. Inherited anonymous stdin/stdout pipes form the private transport; there is no
network listener, loopback port or browser verification endpoint. Protect the installed binaries,
configuration, and secrets against writes by ordinary users. This is a process bridge, not a new
Windows service; the existing Reports Windows service/scheduled task owns its lifecycle.

Only a minimal reply (protocol, nonce, repository, principal scope, status and entry kind) is accepted.
Replies for another request/user/repository, unexpected fields, unknown statuses and oversized output
are rejected. The parent kills child processes on cancellation/timeout/output overflow; an independent 35-second child watchdog bounds orphaned operations if the parent is forcibly terminated. The parent drains both
streams, and bounds concurrent checks to four and admission to twelve checks per minute per identity
bucket (fixed 256-bucket table; collisions may conservatively throttle another identity).

New SDK sessions are intentionally short-lived for targeted diagnostics: each request must re-resolve
identity and rights. This prevents sharing mutable SDK sessions across users, stale authorization caches,
and an uninterruptible native SDK operation hanging the backend. There is no authorization cache and
no retry under another identity. Credentials pass through stdin only, never command-line arguments.
A check has a 15-second default budget, clamped to 2–30 seconds, separate from the hour-long AI timeout.

## Identity

* Password session: require Reports' server session generation, authentication method and scope subject.
  Retrieve only the credentials stored by the existing successful login. The bridge authenticates those
  credentials against the configured repository and obtains `Session.UserIdentity`, a SID. It then
  disposes that user session. This works only for account login methods the installed SDK accepts;
  validate local and LFDS password accounts separately on the target server.
* LFDS SSO: never use `LfdsUser`, display name, `name`, `preferred_username`, or an arbitrary client JWT
  as a trustee. The resolver gets the session-scoped token from the existing backend auth service,
  reads its exact `iss` + `sub`, and requires exactly one administrator-approved mapping to a SID
  **inside that repository's binding**. Opaque tokens, missing claims, duplicate or absent mappings
  return Unknown. The decoder is not a standalone token validator or a public API; its input is only
  the server-issued token held by the auth service after code exchange. No new authentication flow
  or password prompt is added to SSO.
* Windows/AD identities: map the authenticated SSO subject to the Windows trustee SID; alternatively use
  an SDK-supported explicit login. The bridge uses `AccountReference(SecurityIdentifier, session)` and
  confirms it resolves to a user with that same SID. It never impersonates the backend's Windows account
  as the interactive user. New integrated Windows interactive authentication is not implemented here.

The mapped SID must be recorded by an administrator using the repository/organization's authoritative
identity tools, including the LFDS organization. Do not guess it from account names. Automatic conversion
of every LFDS subject to a trustee is not asserted or implemented. A missing reliable mapping is Unknown,
not a user or permission error. The exact API server URL must also match the repository binding.

## Disclosure policy and service identity

Both `AllowedTrusteeSids` and `AllowedEntryIds` must explicitly authorize the resolved target. There are
no wildcard defaults. A name or path is checked against the resolved entry ID before disclosure. Several
hidden matches produce a neutral Unknown response, never names, counts or a privileged clarification list.
The bridge only resolves exact names and paths. Existing user-visible Arabic normalization/spelling
resolution remains in the ordinary search; fuzzy privileged searches are deliberately not implemented.

The service logs in using its Windows identity if `ServiceCredentialFile` is empty. Configure a dedicated
Windows service account linked to the repository, or use a DPAPI CurrentUser-protected dedicated LF service
credential. Neither choice changes the ordinary interactive credential. The service account needs enough
visibility to resolve each authorized target and enough security-inspection rights for `GetEffectiveRights`.
Have the administrator validate the least rights for the intended entry scope; entry Browse + Read entry
security may be sufficient for positive checks, but must be tested on the actual repository.

Do not grant interactive users Bypass Browse, change ACLs or use an administrator for normal reports.
Do not put any password in JSON, environment defaults, Git, logs, installer profiles or browser responses.
`protect-service-credential.ps1 -Path <new-dedicated-directory>\permission.bin` prompts for the internal
LF account, restricts the new empty directory ACL to its owner, SYSTEM and administrators, and encrypts
with DPAPI CurrentUser. Run it **as the Windows identity that actually runs Reports**, on that machine.
A file protected under a developer desktop account will not decrypt under LocalSystem or an IIS pool.
For service deployments the simplest option may be a dedicated linked Windows service identity with no
credential file. Changing the Windows service account itself is an administrator deployment operation.

## Decisions and limitations

| Evidence | Decision exposed |
| --- | --- |
| Normal API read succeeds | Existing user-visible result; no privileged data is used |
| Service resolves one authorized document/folder, approved SID lacks Browse or Read in `Entry.GetEffectiveRights(id, trustee, service)` | Exact requested Arabic document/folder denial |
| Confirmed authorized target/trustee and a real API 403 for the requested operation (backend-only evidence) | Typed denial for that operation, even when entry-level rights alone would allow it |
| Rights are granted, but the original API operation returned 404/nothing | Unknown: tags, filters, field or volume rights, concurrent changes and endpoint behavior are not inferred |
| Completed exact **ID** search has zero hits, explicit full-visibility acceptance AND runtime ReadAccess, BypassBrowse, BypassFilters, SeeAllUserAreas privileges, known requested kind, approved ID and SID | Typed NotFound |
| Missing visibility attestation/privileges, failed SDK call, licensing or dependency error, service cannot inspect security | Neutral Unknown or VerificationUnavailable; never NotFound |
| Hidden name duplicates, no approved disclosure, unresolved trustee, opaque SSO token | Neutral Unknown |
| API 401 / auth renewal failure | Existing sign-in flow; never classified as denial or absence |

`GetAclDisplayRights` is intentionally **not** the authorization predicate: package XML describes it as
ACL-granted rights. The pinned SDK has a documented `GetEffectiveRights(Int32, AccountReference, ISession)`
overload. Its signatures and dependencies were checked against the real NuGet assembly/XML and compiled.
No Cloud AccessControl routes are called. SDK ACL writes, restores and purge APIs are not used.

`FullVisibilityValidated` is an administrator acceptance statement, not a privilege grant. Leave false
unless all security tags, classification restrictions, filters, user areas and other repository overlays
have been tested, including hidden sentinel entries. The runtime privilege check is additional evidence,
not a claim that those flags alone bypass every overlay. An SDK exception never proves absence. Name/path
absence remains Unknown; NotFound is intentionally limited to exact IDs with strong scope evidence.
Security-tag-specific denial is not separately inferred if effective rights grant Browse+Read and there is no observed API 403. BrowseOnly
and Accessible exist in the internal decision model; the bridge does not claim them from a failed API read.
Ambiguity among visible results uses the existing user-only clarification. Hidden ambiguity stays neutral.

OCR candidates are rechecked against current entry and fields reads after ingestion, including entries
already selected before OCR. Disallowed candidates never reach the answer model or sources. As with normal
Repository API sessions, repository-side session/security caching and revocation timing must be tested;
these checks do not promise immediate server-side permission refresh. There is no stored Reports decision.
No permission grant is derived from Supabase. Existing counts still follow the API's current-user scope;
this change does not implement an independent global Browse-versus-Read recount of every general result.

## Configuration example (no secrets)

Merge into the existing administrator-controlled runtime configuration; do not replace unrelated settings.

```json
{
  "PermissionVerification": {
    "Enabled": true,
    "TimeoutSeconds": 15,
    "Repositories": {
      "REPOSITORY_API_ID": {
        "ApiServerUrl": "https://YOUR_API_HOST",
        "ServerName": "YOUR_LASERFICHE_SERVER",
        "RepositoryName": "YOUR_NATIVE_REPOSITORY_NAME",
        "ServiceCredentialFile": "",
        "AllowedTrusteeSids": ["REPLACE_WITH_VERIFIED_TRUSTEE_SID"],
        "AllowedEntryIds": [619],
        "FullVisibilityValidated": false,
        "Subjects": [
          { "Issuer": "EXACT_ISSUER", "Subject": "EXACT_AUTHENTICATED_SUBJECT", "TrusteeSid": "REPLACE_WITH_VERIFIED_TRUSTEE_SID" }
        ]
      }
    }
  }
}
```

Log events include decision status and trace ID only. Secure the existing application log directory and
retention according to the organization's audit policy. There is no toggle that silently disables these
bridge audit events. Existing application diagnostics outside this new component remain unchanged.

## Build, install, offline operation and removal

Build/publish the Web project on Windows x64 as before. Its MSBuild targets restore and separately build
the net48 bridge, copy the entire dependency output plus SDK EULA to `permission-bridge`, and include it in
publish. The existing WiX recursive payload generation and transfer package both include the folder; setup
requires net48 already. Windows CI checks the bridge payload in a clean checkout. Packaging and the legacy
install scripts run `test-permission-bridge.ps1`; it verifies x64, net48 and required payload files only.
The bridge's admin scripts and this document are published alongside it.

* Native installer: create the EXE/MSI through `build/publish.ps1 -Version <new-version>`, then install it.
  Existing Reports service starts the published Web executable; Visual Studio / `dotnet run` are unnecessary.
* Transfer package / source installation: use the existing build-package/install-package or install.ps1
  workflow. The bridge uses the same owning process and is terminated on cancellation (with a child watchdog for forced parent shutdown); no extra port or service is registered.
* Verify deployment: `test-deployment.ps1 -WebPath <installed-web-directory>`. `/health` includes a bridge
  **deployment** check, which does not assert native SDK connectivity/licensing or real LFDS correctness.
* Configure identity/disclosure only after acceptance. Restart Reports if needed to load the runtime config.
* Disable with `PermissionVerification:Enabled=false`. In-flight SDK checks are bounded by their timeout.
* Removal follows existing MSI / unregister safety behavior. No new directory deletion, LF file deletion,
  credential deletion, ACL mutation or database migration is added. Manage any external DPAPI file explicitly
  during credential rotation/decommissioning; it is deliberately not deleted by uninstall.

Runtime verification makes no Internet or Cloud calls. Build/restore requires packages already cached or
an available NuGet feed, as the existing installer build does. Offline first installation of prerequisites
must be staged through the existing installer. No additional NuGet package or SDK binary is downloaded at
runtime. Do not redistribute dependencies beyond the SDK EULA/your license; the payload carries the EULA.
No claim is made that SDK licenses or target-machine native prerequisites were exercised in this environment.

## Acceptance and review

Required automated checks: build the Web/tests and net48 bridge; run PermissionVerificationTests and the
existing Web/Infrastructure suites; clean Windows publish + EXE/MSI acceptance and PowerShell parsing.
The added tests cover hostile targets, replay/cross-user/repository responses, hidden response fields,
neutral failure classification, scope isolation, no authorization caching, rate limiting and content failure.

Before enabling for users, run a real repository acceptance matrix and record account/SID, operation,
expected/actual decision, trace ID, installed build and timestamp (without passwords or hidden content):

1. Local Admin, normal user with Browse+Read, Browse without Read, no Browse.
2. LFDS SSO issuer/sub mapping and LFDS password login; duplicate display names in different organizations.
3. Windows/AD SID mapping and multiple groups, explicit/mixed/inherited Deny, security tags and filters.
4. Existing hidden document and folder, deleted/moved ID, truly absent approved ID, duplicate hidden names.
5. Service lacking Browse/security inspection/full visibility; SDK unavailable, timeout, licensing error.
6. Logout A then login B, repository/server switch, expired SSO, permission change while OCR/model is running.
7. General searches/counts and OCR snippets do not disclose protected sources; no open links on denial.
8. Installed service (not VS), repair, upgrade, uninstall preserving LF and external data, disconnected Internet.

If a row cannot be proven, keep disclosure off for that scope and report Unknown. This implementation
intentionally does not assert the full original Definition of Done before these live checks. Automatic
LFDS subject discovery, privileged fuzzy resolution, comprehensive per-operation field/volume/tag diagnosis,
and immediate repository-side revocation guarantees remain outside the validated scope.

## Implementation validation record (2026-11-01)

Base commit: `1f63ff98b2aa14c65f78a694f5d779426c04d002` on
`fix/typed-semantic-repository-plans`; independent implementation branch
`feat/permission-verification-bridge`. No changes from the abandoned PR #43 were applied.

* Web + test projects: compiled against .NET SDK 8.0.427. Existing ASP.NET analyzer/platform warnings
  were observed; no compiler errors remained.
* net48 bridge: restored the actual pinned NuGet package and compiled successfully, zero warnings/errors.
* Web suite: 226 cases, 225 passed, one explicitly skipped PostgreSQL/pgvector database integration case.
* Infrastructure suite: 308 cases, all passed. Total: **533 passed, one skipped**, including 21 new cases.
* VSTest itself failed inside this sandbox's process/diagnostics environment. Both suites were subsequently
  discovered/executed by the real xUnit framework in an in-process temporary harness with parallelism
  disabled. The harness was not added to production source. These are not native Windows test results.
* `git diff --check`: passed.
* Not executed here: live Laserfiche SDK calls, LFDS/AD trustee resolution, security-tag and inheritance
  acceptance, Windows credential/ACL scripts, offline EXE/MSI installation/repair/upgrade/removal, and
  the database-dependent hybrid integration case. Windows CI and target-machine acceptance remain gates.

Changed responsibilities: `Permissions/` holds server principal resolution, policy-bound protocol,
admission/budgets and the process adapter; `tools/permission-bridge/` holds SDK-only operations;
`LiveRepositoryReportService`, `EntryAvailabilityReport`, `ReportsChatService`, `RepositoryReadError`
and `Program` connect safe outcomes to the existing search/content/HTTP flows. The Web project and
Windows build/install scripts package the component; CI asserts the shipped payload. Tests are in
`PermissionVerificationTests`, `PermissionPrincipalTests` and `PermissionSearchTests`.

### CI observation and resolved baseline failure

The first PR run (`Verify Reports`, run 124) passed clean Windows SDK payload build, the normal
Windows .NET test invocation, PowerShell syntax parsing, source identity checks, transfer packaging,
and the PostgreSQL hybrid integration job. These checks do not establish live LFDS/SDK behavior.
EXE/MSI and UI jobs were still running when this observation was recorded; use the PR checks for
current status, including the follow-up commit's rerun.

The Python job ran 264 tests and failed
`LivePlannerTests.test_calendar_period_can_combine_with_tag_and_numeric_conditions_for_one_count`
(`2 != 3` conditions; calendar filter removed during catalog grounding). This was reproduced in a
separate archive of the unchanged base commit `1f63ff98b2aa14c65f78a694f5d779426c04d002`.
The follow-up requested from that failure log fixes `scope_grounding.without_unasked_fields` to
retain an explicit structured period when its year is mentioned outside grounded catalog names.
It checks numeric boundaries and normalized Arabic/Persian digits, preserving the existing removal
of invented conditions. Regression tests cover combined date/tag/numeric filters, folder scope,
unasked years, years inside tag names, and substrings of unrelated numbers. The original failing
planner test now passes. The full Python suite runs 267 cases: **266 passed, one skipped**.
Current overall CI status must still be checked on the PR; live SDK acceptance requirements remain.

### UI runner hang resolved

On run 126, Windows build/.NET tests/MSI install-repair-upgrade-removal, Python and PostgreSQL
integration all completed successfully. The UI job remained open after 25 tests passed and three
assertions failed: an app keepalive interval survived because cleanup was placed after assertions.

The follow-up registers unconditional `afterEach` cleanup for every happy-dom window (including
secondary export documents) and closes its timers/fetches on both pass and failure. The three stale
assertions now verify the current report-info Saudi timestamp, a full report HTML download through
the details drawer, and the current navy Excel header fill. No product UI/export behavior is changed.
The runner does not use forced exit or suppress failing assertions.

Validation: before the fix the suite exceeded a 12-second reproduction deadline; with cleanup alone
it returned failure normally (25 passed / 3 failed, about 1.4 seconds). After correcting expectations,
**all 28 UI tests passed**, zero skipped, and the process exited successfully in about 1.4 seconds
locally. The runner has a 30-second test timeout, the CI test step a two-minute limit and the UI job
a five-minute limit. New workflow runs use a per-PR concurrency group to cancel superseded grouped
runs. Already-running older runs created without that group require manual cancellation in Actions.
