# Post-search authorization review — 2026-11-05

Status: review implementation; **not accepted for production until live Windows/Laserfiche tests pass**.

## Source and scope

Started from the newest related remote commit observed on 2026-11-05:
`0ec180a693cdff53365c36a2734e645df87d43e2`, branch
`fix/permission-failure-diagnostics-20261104` (PR #48). The primary project branch
`fix/typed-semantic-repository-plans` was `90a450d3b076b4804c9d8622cc611a39259a646b`.
Both include the stable reference `d824254754ce856d26ffe220cec1dac260ce286a`.
No reset, merge, ACL update, production deployment or installer replacement was performed.
This is a stacked review on PR #48 so its existing diagnostics are preserved without mixing
them into this change's diff. Merge/rebase that dependency before integrating into the primary branch.

## Findings and behavior

* The existing HTTP handler checks a candidate collection after the API returns it and before
  deserialization into reports. Previously a single unknown decision aborted the entire page.
* `PostSearchAuthorizationFilter` now retains only independently allowed candidates, bounded to
  four concurrent checks. A denied candidate is excluded. Unknown decisions and transient requested-data
  failures exclude that candidate and mark the request incomplete. Authentication failures and caller
  cancellation still abort. Order and continuations are preserved; candidate counts are removed.
* The existing gateway still requires the SDK's complete ancestor chain including root, effective
  Browse/Read, and current interactive-user API reads. The SDK, identity resolution, search submission,
  query construction, planner and authentication code are unchanged.
* Null/incomplete native repository bindings now fail closed instead of dereferencing null.
  This does **not** create a valid binding or install/configure SDK credentials on a user's machine.
* Search totals become non-exact after unknown verification. Ordinary listing reports can show
  verified rows, with a visible Arabic partial-result notice on the answer and every report section.
  Scopes are non-exhaustive; exports/history retain that notice through the existing result objects.
  Exact-only aggregations/grounding may still refuse incomplete data rather than inventing totals.
* Complete SDK failure cannot become a successful empty answer. Receipts and protected-operation
  revalidation remain mandatory. Revocation/failure on a dependency already used in a composed report
  can still invalidate that whole snapshot; no unsafe dependency is dropped to force an answer.

## Changed-file responsibilities

| File | Reason |
| --- | --- |
| Application/Interfaces/IFullHierarchyPolicy.cs | Carry request-local incompleteness to search totals |
| Infrastructure/Services/LaserficheSearchService.cs | Mark filtered totals non-exact; search execution unchanged |
| Web/Permissions/PostSearchAuthorizationFilter.cs | Candidate-level allowed/denied/unknown handling and partial-result notices |
| Web/Permissions/HierarchyAuthorizationHandler.cs | Invoke the shared filter after receiving candidate rows; verify projected-field operation before disclosure |
| Web/Permissions/FullHierarchyAuthorizationService.cs | Null-safe binding rejection and request-local incompleteness |
| Web/LiveRepositoryReportService.cs | Allow a verified partial listing without claiming completeness |
| Web/Program.cs | Register filter and mark final report before issuing existing receipt |
| Web.Tests/PostSearchAuthorizationFilterTests.cs | Mixed decisions, operation denial/unknown, authentication, cancellation, isolation and partial sections |
| Web.Tests/HierarchyAuthorizationHandlerTests.cs | Transport disclosure/order and unknown-candidate regressions |
| Infrastructure.Tests/LaserficheSearchPagingTests.cs | Preserve paging and reject exact totals after incomplete verification |
| This document | Review evidence and live release gates |

Paths above are under `src/`, except this document. No planner, JS/CSS/HTML, SDK, login,
model configuration, Recycle Bin, OCR, export format or installer implementation changes.

## Automated verification

* .NET SDK 8.0.427: solution restore/build succeeds on Linux. Existing Windows-image and
  ASP.NET analyzer warnings remain; no compiler error.
* Web xUnit: 280 passed, 1 skipped (external PostgreSQL integration).
* Infrastructure xUnit: 318 passed.
* Python planner regression: 285 passed, 1 skipped.
* Existing UI/export/session regression: 29 passed.
* `git diff --check`: passes.

VSTest cannot start normally in this host's process/diagnostics environment. The .NET tests
were therefore executed by xUnit's actual AssemblyRunner in-process, parallelism disabled,
using a temporary runner outside the repository. This is not a Windows/native integration run.
No test dependency or runner is shipped in the product; no existing test is removed.

## Required live acceptance — NOT EXECUTED

There is no connection here to the user's on-premises LF server or Windows service, and no
user/service credentials were supplied. Synthetic IDs in tests are not evidence about real ACLs.

1. Build the Windows published payload with the existing build/installer process. Confirm both
   net48 readers and the pinned SDK dependencies are included. A source update alone does not
   replace an installed service's binaries.
2. Preserve the installation's current configuration. Configure its administrator-owned
   `PermissionVerification:Repositories` binding with the actual API URL, native server/repository,
   and existing service identity/DPAPI credential procedure. Do not guess native names or use
   administrator tokens for report reads. LFDS SSO still needs the existing authoritative subject/SID
   mapping; this patch does not remove that requirement.
3. Run the shipped `permission-bridge/test-deployment.ps1` and authenticated
   `/api/reports/authorization/status`. `ready_for_live_validation` proves static preflight only,
   not a successful connection, identity resolution, licensing or effective-rights calculation.
4. Log in as `amjd` in `testemployee`. Check document `553`, parent `18`, every actual ancestor and root.
   If any required level is denied, verify exclusion from the table, AI evidence, links, totals and exports.
5. Repeat as a genuinely fully authorized account; prove that 553 appears. Repeat with explicit deny,
   group/inherited permissions, LFDS/local/AD identities, tags, fields, volumes and repository switching.
6. Test one unknown candidate beside independently allowed results, all-unknown failure, continuation
   through a filtered-empty page, saved report revocation, and installed offline execution.
7. Measure representative large queries. No real performance or installed/offline acceptance result
   is claimed here. Existing per-candidate SDK process/time/verification budgets remain unchanged.

Missing repository configuration, SDK licensing, service logon rights and actual LFDS account resolution
remain deployment prerequisites. Do not merge/deploy this draft as proof that those are fixed on a host.
