# Incident Summary

**Title:** Intermittent 500 errors on task creation + UI rendering bugs (duplicates/ordering)
**Date:** 2026-05-21
**Severity:** Sev-2 (user-facing errors on a core workflow; no data loss)

## Impact
- **Users affected:** All users of the task tracker. Any user creating a task had a ~35% chance of hitting a 500 error from the bundled frontend. External API consumers sending requests without the `X-Client-Timestamp` header hit 500 on every request.
- **Symptoms observed:**
  - "I click Add and sometimes it just errors. If I try again a few seconds later it works." (Report 1)
  - "We're seeing 500 errors on task creation in production. We can't reproduce in our environment." (Report 4)
  - "After I refresh, some tasks show up twice. Also the order looks random sometimes." (Report 3)
  - "My task list takes a long time to load. My coworker says it's fine." (Report 2)

## Detection
- Customer support reports from multiple users (Reports 1, 2, 3)
- Engineering flagged production 500 errors from server logs but could not reproduce locally (Report 4)
- Production log artifact (`artifacts/sample_api_log.txt`) provided by engineering showed the stack trace

## Timeline (UTC)

- **Unknown** — Bugs introduced at initial deployment (present in the original codebase)
- **2026-05-21 ~15:00** — Investigation began. Reviewed source code and log artifacts
- **2026-05-21 ~15:10** — Root cause identified for Reports 1 & 4: `DateTime.Parse` on unvalidated header in `TaskEndpoints.cs:41`
  - Diagnosed from `artifacts/sample_api_log.txt`:
    ```
    CreateTask request UserId=user-001 Title=Buy groceries X-Client-Timestamp present=False length=0
    System.FormatException: String '' was not recognized as a valid DateTime.
       at System.DateTime.Parse(String s)
       at ...TaskEndpoints.cs:line 44
    ```
- **2026-05-21 ~15:15** — Root cause identified for Report 3: `main.js:46` uses `concat(items)` instead of replacing state, causing duplicates on every refresh. Sort is ascending (oldest-first), contradicting server's descending order.
- **2026-05-21 ~15:20** — Root cause identified for Report 2: `TaskEndpoints.cs:17` loads entire Tasks table into memory before filtering. Confirmed via `artifacts/sample_slow_list_log.txt` showing 1847ms for limit=200.
- **2026-05-21 ~15:30** — Fixes applied for Reports 1, 3, and 4. Server-side: `TryParse` with `UtcNow` fallback. Client-side: removed random empty-header logic, replaced `concat` with assignment, fixed sort direction.
- **2026-05-21 ~15:35** — 12 integration tests written and passing, covering all fixed scenarios
- **2026-05-21 ~16:00** — Fix applied for Report 2: moved `Where`, `OrderByDescending`, and `Take` before `ToListAsync` so EF Core pushes filtering to SQL. All 12 tests passing.

## Root cause

### Reports 1 & 4 — Create task 500s
`TaskEndpoints.cs:41` called `DateTime.Parse(clientTimestamp)` **before** input validation (line 43). When the `X-Client-Timestamp` header was missing or empty, this threw an unhandled `FormatException` → 500.

Two separate triggers:
- **Report 1 (local):** `main.js:59` had `Math.random() > 0.35` — 35% of requests sent an empty header
- **Report 4 (production):** Header was entirely absent (`present=False`), likely from a different client or infrastructure stripping the custom header

### Report 3 — Duplicates / ordering
`main.js:46` used `state.tasks.concat(items)` which appended server results to existing state on every refresh. Sort used `String.localeCompare` (ascending), contradicting the server's newest-first order.

### Report 2 — Slow lists
`TaskEndpoints.cs:17` called `db.Tasks.AsNoTracking().ToListAsync()` loading all rows (60k+ locally, likely more in production) then filtered in memory. No `WHERE` or `LIMIT` in the generated SQL. No indexes on `UserId` or `CreatedAt`.

## Mitigation / resolution

### Immediate fixes applied

**`TaskEndpoints.cs` (Reports 1 & 4):**
- Moved `IsNullOrWhiteSpace` validation before the timestamp parse
- Replaced `DateTime.Parse(clientTimestamp)` with `DateTime.TryParse` + `DateTime.UtcNow` fallback
- **Design decision:** Non-empty malformed timestamps (e.g. "not-a-date") also fall back to `UtcNow` rather than returning 400. This prioritizes availability — the client timestamp is optional metadata, not a required field. If stricter validation is desired, a future change could reject non-empty invalid values with 400 while still allowing missing/empty headers.
- Blast radius: minimal — only changes error handling for invalid input, valid requests unaffected

**`main.js` (Reports 1 & 3):**
- Removed `Math.random() > 0.35` logic; always sends valid ISO timestamp
- Replaced `state.tasks.concat(items)` with `state.tasks = items`
- Changed sort from ascending `localeCompare` to descending `new Date(b) - new Date(a)`
- Blast radius: client-only, no server impact, users get fix on page reload

**`TaskEndpoints.cs` (Report 2):**
- Moved `Where`, `OrderByDescending`, and `Take` before `ToListAsync` so EF Core translates them to SQL
- The application no longer materializes the full Tasks table before filtering. EF now generates SQL with `WHERE`, `ORDER BY`, and `LIMIT`. A composite index is still needed to avoid database-level scans as the table grows — see `TICKET.md`.
- Also fixed the logged `limit` value to reflect the effective (clamped) limit, not the raw input
- Blast radius: minimal — same filtering logic, just executed in SQL instead of C#. API contract unchanged.

## Verification

### Automated tests (12/12 passing)
| Test | Verifies |
|------|----------|
| `CreateTask_ShouldReturn201_WhenValid` | Happy path with valid timestamp |
| `CreateTask_ShouldReturn201_WhenTimestampMissing` | Report 4: no header → 201, not 500 |
| `CreateTask_ShouldReturn201_WhenTimestampEmpty` | Report 1: empty header → 201, not 500 |
| `CreateTask_ShouldReturn201_WhenTimestampInvalid` | Invalid value → 201, not 500 |
| `CreateTask_ShouldReturn400_WhenUserIdMissing` | Validation: empty userId → 400 |
| `CreateTask_ShouldReturn400_WhenTitleMissing` | Validation: empty title → 400 |
| `CreateTask_ShouldUseProvidedTimestamp_WhenValid` | Valid timestamp is persisted correctly |
| `ListTasks_ShouldReturnOnlyRequestedUser` | User filtering returns correct data |
| `ListTasks_ShouldReturnNoDuplicates` | Report 3: API returns unique task IDs |
| `ListTasks_ShouldReturnOrderedByCreatedAtDescending` | Report 3: newest-first ordering |
| `ListTasks_ShouldRespectLimitParameter` | Report 2: limit parameter caps result count |
| `ListTasks_ShouldNotLeakTasksBetweenUsers` | Report 2: user filtering has no cross-user leakage |

**Note on Report 3 test coverage:** The duplicate/ordering bug was client-side (`main.js` state management). The automated tests verify the API returns correct data (no duplicates, correct order), but cannot exercise the browser-side `concat` bug. The client fix is verified via the manual UI steps below (DevTools row count check after repeated refreshes).

### Manual verification steps
- Swagger (`/swagger`): `POST /api/tasks` with valid body → 201 (Swagger doesn't send `X-Client-Timestamp`, so this directly verifies the Report 4 fix)
- Swagger: `GET /api/tasks` with `userId=user-001` → verify only user-001's tasks returned
- Click Add in UI 20+ times → no 500s in server log
- `curl` POST without header → 201
- Click Refresh repeatedly → row count stays stable, no duplicates
- New tasks appear at top of list (newest-first)

## Follow-ups / action items
- [x] ~~**P2:** Push list query filtering to the database~~ — done
- [ ] **P2:** Add composite index on `Tasks(UserId, CreatedAt)` and performance guardrails — see `TICKET.md`
- [ ] **P3:** Investigate missing `X-Client-Timestamp` header in production. Log shows `present=False` from an unknown client. Possible causes: different API consumer, reverse proxy stripping custom headers, or corporate proxy. Consider logging `User-Agent` to identify the source. Decide whether `X-Client-Timestamp` should remain client-provided or if server should always set `CreatedAt`.
- [ ] Add structured error logging / alerting for unhandled exceptions (the 500s were only caught via manual log review)
- [ ] Add browser-level test (e.g. Playwright) for the client-side duplicate/ordering fix to complement the API-level tests
