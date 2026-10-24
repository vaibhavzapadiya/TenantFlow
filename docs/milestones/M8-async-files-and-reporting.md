# M8 — Async files and reporting

**Goal:** a tenant searches its catalog, and imports and exports products as CSV files, without a request ever doing
long work, without one tenant's job touching another's data, and with every job bounded, resumable where it must be,
and visible to operators.

**Gate (roadmap):** large jobs are asynchronous, tenant-scoped, bounded, resumable where needed, and observable.

Spec: [SPEC-0008](../specs/0008-async-files-and-reporting.md) (Implemented). Decision:
[ADR-0024](../architecture/decisions/0024-object-storage-and-async-import-export-jobs.md) (Accepted 2026-10-21). Open
Decision 19 (object storage) is closed by it: a storage port with a filesystem adapter; the S3/GCS adapter comes with M12.

## 1. What was built

```text
src/
├── BuildingBlocks/Abstractions/Workflow    ScheduledTask: a module's recurring task (name, cron, run), no tenant set
├── BuildingBlocks/Messaging/Timers         DueByKindAsync (kinds, counts, oldest due; no ids); the schedule upsert
│                                           never re-arms another tenant's due row
├── BuildingBlocks/Scheduling               ScheduledTaskJob: one generic Quartz job for every task; tasks removed from
│                                           the code are deleted from the clustered store on start
├── Hosts/Api/Endpoints/V1                  Products ?q= search; Tenants/ImportsExports: imports (multipart upload, 5 MB
│                                           route limit, status, row errors) and exports (submit, status, download)
├── Hosts/Worker                            ImportsExports module, its due rows, the imports_exports.metrics task (30 s)
└── Modules/
    ├── Catalog                             stored generated search_vector + GIN index; ICatalogImport (exactly-once
    │                                       batches in catalog.import_batches); ICatalogExport (keyset pages)
    └── ImportsExports (new)                IObjectStorage port + filesystem adapter (server keys, root containment,
                                            atomic writes, checksum verified on read); StoredFiles; import and export
                                            jobs, runners and stores; CSV reader/cells; file expiry; metrics
```

Tables: `imports_exports.stored_files`, `import_jobs`, `import_row_errors`, `export_jobs` and `catalog.import_batches`
(forced RLS, 20 forced tables in all); `imports_exports.workflow_timers` (ids only, outside RLS as in every module).
Six new routes: the isolation matrix covers 31.

## 2. Architectural reasoning

- **A request records intent; the Worker does the work.** Submitting an import commits the file row, the job
  (`receiving`) and its abandon due row; then the upload is written to storage, outside any transaction; then the job is
  queued with its run due row. Every crash point leaves a row that says what happens next (rule 3).
- **Exactly once where it matters, idempotent elsewhere.** An import batch is applied by Catalog together with its
  `(tenant, job, batch)` row, so a re-run replays the stored result instead of upserting twice; the checkpoint moves only
  from the value the run read. An export has no partial state worth keeping: a restart rewrites the whole file, and
  storage only ever shows a complete one.
- **Module boundary kept.** ImportsExports reaches Catalog only through `Catalog.Contracts`; Catalog applies its own
  product rules, so an imported row is validated exactly as an API call would be.
- **A port, not a vendor.** `IObjectStorage` has three operations and a filesystem adapter; S3/GCS (M12) is a new adapter.
- **Quartz for recurring work, due rows for business dates.** The metrics refresh is a cron task with no tenant; job
  runs, abandons and file expiry are due rows written in the transaction that needs them (ADR-0023 pattern).
- **Failure ends a job.** Permanent errors fail it with a code; anything else fails it on the last timer attempt
  (`run_failed`); stale active jobs stop counting toward the limit. A tenant can never be locked out by a broken job.

## 3. Security considerations

- Tenant context from the membership, never the route: every store filters by the context tenant inside a
  `TenantScopedTransaction`, RLS is forced on all five new tables, and storage keys are made by the server and name their
  tenant, checked again on every read, write and delete. Due rows of another tenant never act on a job or file (tested).
- Uploads: `catalog.manage`, read in the handler after the tenant filter; a per-route 5 MB + 64 KiB body limit (64 KiB
  stays the default), at most 4 form parts, the key checked before the body; UTF-8 only, fields at most 1,000 characters
  and 32 per row, at most 10,000 rows.
- Downloads: `catalog.manage`, 404 to other tenants, streamed with the checksum verified; `no-store` and `nosniff` from
  the security headers middleware; a GUID file name.
- CSV injection: every cell quoted; text starting with `= + - @`, tab, CR or the full-width `＝ ＋ － ＠` gets a leading
  `'` (OWASP); an import restores it.
- Row errors hold row, column and code, never the value; metric labels are kind and outcome only.
- Search: only letters and digits reach `to_tsquery`; a one-character last word is matched whole.

## 4. Verification

Fresh evidence for the gate (2026-10-22) at `f0dac36`:

| Command | Result |
|---|---|
| `./scripts/verify.ps1 -Database` | all checks passed; Tenants 77/77, IdentityAccess 68/68, Catalog 9/9, Architecture 7/7, Inventory 5/5, Orders 6/6, Payments 14/14, Subscriptions 7/7, ImportsExports 26/26, Worker 14/14, Api 128/128, Database 284/284; 0 warnings. A first run passed every test but failed its leftover-database check (environment); the rerun passed whole |
| `./scripts/progress.ps1 -Verify` (CI test command with coverage) | quality run passed for `f0dac36`: 0 warnings, 361/361 non-database tests, Compose valid. Line coverage of the new code is low here (ImportsExports.Application 35%, Infrastructure 30%, Domain 44%, Scheduling 29%) because the coverage run excludes the database tests, where it is exercised |

**Break-it checks re-run for the gate** (fault injected, test failed at an assertion, fault restored, rebuilt):
- **Tenant-scoped:** no RLS and no tenant predicate on import jobs (cross-tenant test fails).
- **Bounded:** the active-job limit off by one (submission test fails).
- **Resumable:** a batch applied twice after a crash (resume test fails).
- **Observable:** the metrics task records no snapshot (metrics test fails).
- **Deferred proofs:** search words joined by OR; any header accepted; the row limit off; a restart that cannot
  overwrite the export file; the export row limit off. Each made its test fail.

Slice proofs (J1–J12, K1–K7, L1–L7) and review-fix proofs (R1–R18) are in the spec's Verification table and the tracker.
The upload limit is proven with a stand-in for Kestrel's body limit, because `TestServer` does not enforce it.

**Environment notes:** role provisioning in the WSL Docker failed before tests ran in many single-class runs (more often
than in M7); every affected run passed on retry, and both full gate runs passed.

**Not verified:** an upload against the real Kestrel host (the route limit is shown through routing metadata and the
stand-in); two Worker processes against one Quartz cluster; CI (on hold).

## 5. Trade-offs and assumptions

- **Filesystem storage, shared by the API and the Worker** (`Storage:Root`); fine on one host or a shared volume, not
  across hosts. S3/GCS and signed URLs wait for M12.
- **Exports restart from the start.** A run longer than the 2-minute due-row lease could run twice; the later write then
  fails its checksum on download (it fails safe, never serves a wrong file). A file per run would remove it.
- **Background jobs do not re-check the requester.** A manager removed, or a tenant suspended, after submitting still has
  the import finish (deferred review item L10; a spec decision for you).
- **Prices export with the column's scale** (`1.0000`); import reads them back.
- **A SKU like `'-5 pack` changes on an export-then-import round trip** (deferred L12).
- **File permissions and leftover temporary files** after a crash are not handled (deferred L14, M9/M12).

## 6. Mentor exercises

1. Remove `catalog.import_batches` and rely on "upsert by SKU is idempotent". Construct the crash that makes the
   created and updated counts wrong.
2. Bind the upload as an `IFormFile` parameter again. Which caller learns what, before which refusal?
3. Run the export inside one transaction that reads all products and writes the file. Name two rules it breaks.
4. Make the metrics task read `import_jobs` instead of due rows. What must change about tenant context and RLS?
5. Let the abandon due row end any active job. Write the interleaving with a slow upload that loses a good import.

## 7. Interview talking points

- "How do you import 10,000 rows safely?" Commit intent, store the file, queue; the Worker applies batches of 500, each
  exactly once with its own record, checkpoints only forward, and fails the job with a code on anything permanent.
- "How do you keep exports from becoming a weapon?" CSV injection neutralized per OWASP, every cell quoted, downloads
  only for managers of the tenant, no caching, checksum verified.
- "What if a job keeps failing?" It ends: permanent errors at once, others on the last retry, and a stale job stops
  holding the tenant's slot.
- "Why a storage port?" Three operations, a filesystem adapter for development, a cloud adapter later, with no change
  to the jobs.

## 8. Exit checklist

- [x] Imports and exports run asynchronously in the Worker from due rows; requests only record intent (`ImportJobTests`, `ExportJobTests`)
- [x] Tenant-scoped: RLS on 20 tables, isolation matrix 31 routes, cross-tenant jobs, files and due rows 404 or no-op (proofs re-run)
- [x] Bounded: 5 MB uploads (route limit), 10,000 import rows, 100,000 export rows, 32 fields, 2 active jobs per kind (proofs re-run)
- [x] Resumable: imports resume from checkpoints, exactly-once batches; exports restart and overwrite (proofs re-run)
- [x] Observable: due-job gauges every 30 s from Quartz, job outcome counters, failed rows, durations (proof re-run)
- [x] SPEC-0008 Implemented: criteria 1-12 and the review fixes have verification rows
- [x] Reviews: tenant isolation (5 low), correctness (3 high, 2 medium, 5 low), security (2 high, 1 medium, 6 low); F1-F8
      and L1-L9 fixed in `f0dac36` by your choice, L10-L15 deferred as listed
- [x] ADR-0024 accepted by you on 2026-10-21; Open Decision 19 closed
- [ ] **You:** review this guide and the M8 commits on branch `m8-files-reporting`, and merge into `main` when satisfied
- [ ] **You:** decide L10 (background jobs and removed members or suspended tenants)
- [ ] **You:** approve the start of M9 (security and privacy)
