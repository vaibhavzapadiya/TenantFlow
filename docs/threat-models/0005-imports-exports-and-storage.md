# Threat model: imports, exports and storage

- Status: Draft (M9), written with SPEC-0009 S1
- Date: 2026-10-22
- Owner: TenantFlow tech lead (the user)
- Scope: the CSV import upload (multipart, 5 MB route limit), import jobs and their row errors, export jobs and file
  downloads, the `IObjectStorage` port and its filesystem adapter, file expiry and abandon due rows, the
  `imports_exports.metrics` task, and product full-text search (`GET .../products?q=`).
- Related: blueprint sections 9, 10, 13, 16; ADR-0024; [SPEC-0008](../specs/0008-async-files-and-reporting.md);
  [SPEC-0009](../specs/0009-security-and-privacy.md) (AC 2, 7, 8, 11, 12); [M8 guide](../milestones/M8-async-files-and-reporting.md)
  (deferred L10, L14); [threat model 0001](0001-tenant-boundary.md)

## Assets and data classification

| Asset | Classification | Why it matters |
|---|---|---|
| Uploaded CSV files (`import_upload` objects) | Tenant-confidential | The tenant's catalog, prices and status; kept 7 days after the import ends |
| Export files (`export` objects) | Tenant-confidential | The whole catalog, inactive products included; downloadable for 7 days |
| `stored_files` rows (key, size, SHA-256, expiry) | Internal, integrity-critical | The only path from a request to an object; the checksum proves the content |
| `import_jobs`, `export_jobs`, `import_row_errors`, `catalog.import_batches` | Tenant-confidential | Status, counts, the requesting membership id, idempotency key and request hash; row, column and code (never a cell value) |
| Storage root (`Storage:Root`) and its files on disk | Confidential | Holds every tenant's objects in one tree, shared by the API and the Worker |
| Product search index (`search_vector`) | Tenant-confidential | Derived from names and SKUs |
| Metrics (queue depth, oldest due age, outcomes, durations) | Internal | Labels are kind and outcome only |

Personal data: none by design. Product names and SKUs are business data, but a tenant can type anything into them, so
uploads and exports are handled as if they could hold personal data. Job rows hold a membership id (pseudonymous).

## Actors and trust boundaries

Actors: catalog manager (`catalog.manage`); customer user (`catalog.read`, search only); member of another tenant;
removed member or member of a suspended tenant; the Worker (service identity, tenant from the due row); support
operator (SPEC-0009 AC 17, read-only job view, no file access); host operator with filesystem access.

| Boundary | Crossed by | Control |
|---|---|---|
| B1 Internet → API | upload, poll, download, search | JWT validation, fallback deny policy (0001) |
| B2 User → tenant | every route under `/v1/tenants/{tenantId}` | active membership plus `catalog.manage` (`catalog.read` for search) |
| B3 API/Worker → PostgreSQL | job, file and error rows | tenant-scoped transaction, forced RLS on the five tables |
| B4 API/Worker → storage root | put, open, delete | server-made key `tenants/{tenantId}/{kind}/{fileId}`, root containment, key-tenant guard |
| B5 Worker → Catalog | batches and pages | `Catalog.Contracts` only (ADR-0002), product rules applied by Catalog |
| B6 Exported file → user's spreadsheet | download | formula neutralizing, every cell quoted |

## Entry points and data flows

1. `POST .../imports/products`: key checked → tenant and permission filter → form read (≤ 5 MB + 64 KiB, ≤ 4 parts) →
   content type must be `text/csv` or `application/csv` → hashed in memory → intent commit (file row, job `receiving`,
   abandon due row) → object written outside any transaction → job `queued` with its run due row.
2. Worker import run: due row → job tenant set → scan pass (UTF-8, header, field and row bounds, ≤ 10,000 rows,
   checksum) → batches of 500 to `ICatalogImport` → checkpoint → `succeeded` or `failed` with a code.
3. `GET .../imports/{id}` and `.../errors`: tenant-scoped lookup, 404 for anything outside the tenant.
4. `POST .../exports/products` → Worker export run: Catalog pages → CSV stream (neutralized, quoted, ≤ 100,000 rows,
   ≤ 512 MB) → temp file and rename → job `succeeded` with size, checksum and a 7-day expiry.
5. `GET .../exports/{id}/file`: tenant-scoped job and file lookup → key guard → streamed with the checksum verified at
   the end; a GUID file name; baseline headers.
6. Expiry due rows delete files after 7 days; an upload still `receiving` after one hour is failed and deleted.
7. `GET .../products?q=`: only letters and digits reach `to_tsquery`; `q` over 100 characters is 400.

There is no URL input anywhere in this area: no import from a link, no webhook target, no callback. Server-side request
forgery has no entry point today (IE-09).

## Abuse cases / attack paths

| Id | Attack | Attacker goal | Path |
|---|---|---|---|
| IE-01 | Oversized or many-part upload | Exhaust memory or disk | Large body, many form parts, slow body |
| IE-02 | Parser bomb or wrong format | Exhaust memory or CPU in the Worker, or confuse the parser | Not UTF-8, unclosed quote, huge field, wide row, millions of rows, a zip or Excel file renamed |
| IE-03 | Malware-laden upload | Spread malware through stored files | Upload a file carrying a payload |
| IE-04 | CSV formula injection | Run a formula on a colleague's machine | Product name or SKU starting with `=`, `+`, `-`, `@`, tab, CR or full-width forms, then exported |
| IE-05 | Path traversal or key tampering | Read or overwrite files outside the tenant's prefix or the root | `../`, absolute path, another tenant id or a trailing newline in a key |
| IE-06 | Cross-tenant job or file access | Read, poll or download tenant B's job or file | Tenant A's route with B's job id; a due row of B; a file row whose key names B |
| IE-07 | Tampered or replaced object | Serve or import altered content | Change a file on disk between write and read |
| IE-08 | Download caching or sniffing | Leak an export through a shared cache, or get it rendered as HTML | Proxy or browser cache; content-type sniffing |
| IE-09 | Server-side request forgery | Reach internal services | None: no URL input exists |
| IE-10 | Noisy tenant | Starve the Worker or the database | Many jobs, 100,000-row exports in a loop, repeated uploads |
| IE-11 | Stale authority in a background job | Keep importing after removal or suspension | Submit, then lose `catalog.manage` or have the tenant suspended |
| IE-12 | Local file exposure | Read objects as another local user, or recover crash leftovers | Default file modes; `.tmp` files left after a crash |
| IE-13 | Sensitive data kept too long or leaked | Read old uploads, job rows or telemetry | Retained rows past their use; cell values in errors, logs or metric labels |
| IE-14 | Repudiation | Export or download the catalog without a trace | No audit record of the request or the download |
| IE-15 | Duplicate export run | Serve a wrong or mixed file | A run longer than the 2-minute lease runs twice and both write the object |
| IE-16 | Storage not shared across hosts | Lost or unreadable files on scale-out | API and Worker on different hosts without a shared volume |
| IE-17 | Search query abuse | Inject query operators or force expensive scans | `&`, `|`, `!`, `:*` in `q`; one-character prefixes; very long `q` |
| IE-18 | Idempotency replay by another member | Learn another member's job through a shared key | Reuse a key another member used |

## Existing controls

- Upload: per-route limit of 5 MB + 64 KiB and at most 4 form parts; the server-wide 64 KiB limit stays for every other
  route; the key is refused before the body is read; the form is read after the tenant filter; non-CSV content types and
  empty files are 400; files over 5 MB are 413 (`ImportJobTests`).
- Parser: strict UTF-8 (other encodings, including binary zip or Excel content, fail as `file_unreadable`), fields at
  most 1,000 characters, at most 32 fields per row, at most 10,000 rows, exact header; all checked in a scan pass before
  any write (`CsvReaderTests`, `ImportJobTests`).
- Formula neutralizing with a leading `'` and every cell quoted; import restores the value (`ProductCsvTests`,
  `ExportJobTests`).
- Keys are generated by the server, matched against a strict pattern (`\z`, not `$`), resolved and checked to stay under
  the root; a key whose tenant differs from the current tenant is never opened, written or deleted (`StorageTests`,
  `StoredFilesTests`). The host refuses to start without a storage root (`ConfigurationValidationTests`).
- Tenant scoping: forced RLS on the five tables, tenant predicates in stores, 404 for other tenants' jobs and files, and
  due rows of another tenant never act (`ImportJobTests`, `ExportJobTests`, `StoredFileTests`, `JobRecoveryTests`,
  `RowLevelSecurityTests`, `IsolationMatrixTests`).
- Integrity: SHA-256 recorded on write and verified on every read; a mismatch fails the import (`file_corrupt`) or
  aborts the download (`StoredFileTests`, `JobRecoveryTests`). Writes go to a temp file and are renamed into place.
- Responses carry `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, `default-src 'none'` CSP
  (`HttpPipelineTests`, generic; not asserted on the download route itself). Downloads use a GUID file name.
- Bounds on work: 2 active jobs per tenant and kind; a stale job stops counting after 6 hours; a run applies at most
  10 batches per lease; exports bounded by rows and bytes; failed runs end as `run_failed` (`JobRecoveryTests`).
- Expiry: files deleted 7 days after use; abandoned uploads deleted after one hour (`FileLifecycleTests`).
- Data minimisation: row errors hold row, column and code only; metric labels are kind and outcome only
  (`ImportJobTests`, `FileLifecycleTests`).
- Search: only letters and digits reach `to_tsquery`, `q` is at most 100 characters, a one-character last word is
  matched whole (`ProductSearchTests`, `ProductEndpointTests`).
- Idempotency keys are bound to the membership and the file hash; reuse by another member or for another file is 422.
- Uploads are never served back: there is no download route for `import_upload` objects.

## Gaps and residual risk

| Gap | Residual risk | Until |
|---|---|---|
| Jobs do not re-check the requester or tenant status (L10) | A removed manager's or suspended tenant's import finishes | M9 (SPEC-0009 AC 11) |
| Default file modes; no flush before rename; no temp sweep (L14) | Local users may read objects; crash leftovers stay on disk | M9 (SPEC-0009 AC 12) |
| No audit record of import or export requests or downloads | Catalog exfiltration by a manager is not traceable | M9 (SPEC-0009 AC 2) |
| Finished job rows, errors and batches have no purge | Rows kept longer than needed | M9 (SPEC-0009 AC 7, 8) |
| No malware scanning | A hostile file sits on disk for up to 7 days | Accepted until M12 |
| No rate limiting or per-tenant fairness beyond the job cap | One tenant can keep the Worker busy within its cap | M11 |
| Duplicate export run can leave a file that fails its checksum | Download fails; never serves wrong content | Accepted until M12 |
| Filesystem storage shared by one host or one volume | Not usable across hosts; no encryption at rest beyond the host's | M12 |
| Any `catalog.manage` holder of the tenant may download any export | By SPEC-0008 decision 2; not limited to the requester | Owner decision |

## Mitigations with owner and due date

| Id | Abuse case | Mitigation | Owner | Due | Status | Evidence |
|---|---|---|---|---|---|---|
| IE-A01 | IE-01 | Per-route 5 MB + 64 KiB body limit, 4 form parts, key checked first, CSV content type only | Claude | M8 | done | `ImportJobTests.Submission_is_idempotent_bounded_and_for_managers_only`, `ImportJobTests.Only_the_upload_route_reads_more_than_the_server_wide_body_limit_and_a_bad_key_is_refused_first` |
| IE-A02 | IE-02 | Strict UTF-8, field, row-width, row-count and header checks in a scan pass before any write | Claude | M8 | done | `CsvReaderTests`, `ImportJobTests.A_wrong_header_too_many_rows_or_text_that_is_not_csv_fails_the_import_before_anything_is_written` |
| IE-A03 | IE-03 | No malware scanning; files are parsed as text only, never served back or executed, and expire in 7 days | user | M12 | accepted | CSV text only is accepted and parsed, uploads have no download route, and the cloud storage choice in M12 decides whether a scanning service is used |
| IE-A04 | IE-04 | Leading apostrophe on formula starts (ASCII and full-width), every cell quoted, restored on import | Claude | M8 | done | `ProductCsvTests.A_cell_that_starts_with_a_formula_character_is_neutralized_and_restored`, `ExportJobTests.An_export_writes_every_product_with_formulas_neutralized_and_its_file_imports_back_unchanged` |
| IE-A05 | IE-05 | Server-made keys, strict key pattern, root containment, key-tenant guard, required storage root | Claude | M8 | done | `StorageTests.Keys_are_the_servers_shape_and_name_their_tenant`, `StorageTests.Content_is_stored_whole_with_its_checksum_and_only_under_a_valid_key`, `StoredFilesTests.A_file_row_whose_key_names_another_tenant_is_never_opened_written_or_deleted`, `ConfigurationValidationTests.Host_refuses_to_start_without_a_storage_root_and_names_the_key` |
| IE-A06 | IE-06 | Forced RLS, tenant predicates, 404 across tenants, due rows confined to their tenant | Claude | M8 | done | `ImportJobTests.Another_tenant_can_neither_read_an_import_nor_its_errors`, `ExportJobTests.Another_tenant_can_neither_read_an_export_nor_download_its_file`, `StoredFileTests.Another_tenant_can_neither_open_nor_delete_a_file`, `JobRecoveryTests.A_due_row_of_another_tenant_never_acts_on_this_tenants_job_or_file`, `RowLevelSecurityTests`, `IsolationMatrixTests` |
| IE-A07 | IE-07 | SHA-256 on write, verified on every read; a mismatch fails the import or aborts the download | Claude | M8 | done | `StoredFileTests.Content_changed_in_storage_fails_its_checksum_when_read`, `JobRecoveryTests.An_import_whose_file_is_gone_or_altered_fails_at_once_and_frees_its_place` |
| IE-A08 | IE-08 | `no-store`, `nosniff` and a deny-all CSP on every response; GUID download name | Claude | M8 | done | `HttpPipelineTests.Every_response_carries_the_baseline_security_headers` |
| IE-A09 | IE-10 | 2 active jobs per tenant and kind, stale jobs released after 6 hours, bounded batches per run, row and byte caps | Claude | M8 | done | `ImportJobTests.Submission_is_idempotent_bounded_and_for_managers_only`, `ExportJobTests.Submission_is_idempotent_per_member_bounded_and_for_managers_only`, `JobRecoveryTests.Active_jobs_untouched_for_six_hours_no_longer_count_toward_the_limit`, `ProductCsvTests.More_rows_than_allowed_stops_the_stream` |
| IE-A10 | IE-10 | Rate limiting and per-tenant fairness for submissions and downloads | Claude | M11 | planned | M11 workload model |
| IE-A11 | IE-11 | End a queued or running job with `tenant_suspended` or `requester_not_authorized` before further writes | Claude | M9 | done | `ImportJobTests.An_import_stops_before_its_next_batch_once_its_requester_is_demoted_and_keeps_the_batches_applied`, `ExportJobTests.An_export_ends_unwritten_when_its_requester_is_demoted_or_removed_or_its_tenant_suspended` |
| IE-A12 | IE-12 | Files `0600`, directories `0700`, flush before rename, cron sweep of `.tmp` files older than one hour | Claude | M9 | done | `StorageTests.The_sweep_deletes_only_temporary_files_left_for_more_than_an_hour`, `StorageTests.On_unix_stored_files_are_readable_by_their_owner_only` |
| IE-A13 | IE-13 | Expire uploads and exports after 7 days; abandoned uploads after one hour | Claude | M8 | done | `FileLifecycleTests.An_exports_file_and_a_finished_imports_upload_are_deleted_seven_days_later_and_not_before`, `FileLifecycleTests.An_upload_still_receiving_after_an_hour_fails_and_its_file_is_deleted` |
| IE-A14 | IE-13 | Row errors without cell values; metric labels kind and outcome only | Claude | M8 | done | `ImportJobTests.An_import_creates_and_updates_products_by_sku_and_reports_bad_rows_without_their_values`, `FileLifecycleTests.The_metrics_task_reports_due_jobs_per_kind_and_the_runners_count_each_job_that_ends` |
| IE-A15 | IE-13 | Classify the module's tables and purge finished job rows, row errors and import batches after 90 days, tenant by tenant | Claude | M9 | done | `PrivacyDocumentTests.Every_table_in_every_module_schema_has_a_class_a_purpose_and_a_retention_rule`, `RetentionTests.Expired_rows_go_a_row_one_second_inside_its_window_stays_and_another_tenant_waits_for_its_own_run` |
| IE-A16 | IE-14 | Audit records for import requested, export requested and export file downloaded | Claude | M9 | done | `ImportJobTests.An_import_creates_and_updates_products_by_sku_and_reports_bad_rows_without_their_values`, `ExportJobTests.An_export_writes_every_product_with_formulas_neutralized_and_its_file_imports_back_unchanged` |
| IE-A17 | IE-15 | Checksum fail-safe: a file overwritten by a second run fails verification and is never served | user | M12 | accepted | A rare double run only causes a failed download that the user retries with a new export, and a file per run is reconsidered with the cloud storage adapter |
| IE-A18 | IE-16 | Cloud object storage adapter behind the same port, with signed URLs reconsidered | Claude | M12 | planned | M12 cloud storage adapter (ADR-0024 follow-up) |
| IE-A19 | IE-17 | Letters and digits only reach `to_tsquery`, `q` at most 100 characters, one-character last word matched whole, tenant filter and RLS | Claude | M8 | done | `ProductSearchTests.Words_are_joined_the_last_one_is_a_prefix_and_operators_are_dropped`, `ProductEndpointTests.Search_finds_every_word_in_name_or_sku_with_a_prefix_last_word_inside_the_tenant_only` |
| IE-A20 | IE-18 | Idempotency key bound to the membership and the file hash; reuse by another member or for another file is 422 | Claude | M8 | done | `ExportJobTests.Submission_is_idempotent_per_member_bounded_and_for_managers_only`, `ImportJobTests.Submission_is_idempotent_bounded_and_for_managers_only` |

IE-09 needs no action while no URL input exists; adding one is a change trigger below.

## Security tests and monitoring

- Tests: the SPEC-0008 criteria tests cited above, each proven to fail without its guard (SPEC-0008 Verification,
  proofs J1–J12, K1–K7, L1–L7); SPEC-0009 AC 2, 8, 11 and 12 add tests in M9. Not verified: the upload limit against a
  real Kestrel host (a stand-in enforces the body limit in tests), and two Workers against one Quartz cluster.
- Monitoring: queued jobs and oldest due age per kind every 30 seconds; job outcome counters, failed rows and duration.
  A rise in `file_unreadable`, `file_corrupt` or `too_many_jobs` answers is a signal of abuse or tampering; alerting on
  it is M10.

## Review date and change triggers

Review at the M9 gate, then at M11 (rate limiting and workload), M12 (cloud storage, signed URLs, malware scanning
decision) and M13 (tenant offboarding and on-request erasure of stored files). Also review when a new file kind, a new
import or export format (Excel, orders, reports), any URL input, a direct-to-storage upload, a download for someone other
than a tenant manager, or a change to the job cap or file retention is proposed.
