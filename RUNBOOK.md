# Runbook — SupportEngineerChallenge

## Service overview
- **Service:** SupportEngineerChallenge.Api
- **Purpose:** Minimal task tracker (create + list tasks)
- **Data store:** SQLite (`app.db` in the API working directory)
- **Default URL:** `http://localhost:5088` (see README; actual port depends on launch profile — check console output on startup)
- **Seed config:** `appsettings.json` → `Seed:Users` (default 5), `Seed:TasksPerUser` (default 12,000)

## Common commands

**Run locally**
```bash
cd src/SupportEngineerChallenge.Api
dotnet run
```

**Run tests**
```bash
dotnet test
```

## Key endpoints
- `GET /api/tasks?userId={id}&limit={n}` — list tasks for a user (limit 1–200, default 50)
- `POST /api/tasks` — create a task (expects JSON body + optional `X-Client-Timestamp` header)

## Swagger UI
- Available at `http://localhost:{port}/swagger` when the app is running
- Useful for testing endpoints interactively without curl or the frontend
- Note: Swagger cannot set custom headers like `X-Client-Timestamp` through the UI — use curl for header-specific testing

---

## Diagnosing issues in production

### What to look at
- **Server logs:** The app uses structured logging via `ILogger`. Key log lines:
  - `CreateTask request` — logged on every POST, includes `UserId`, `Title`, `X-Client-Timestamp present`, and `length`. Look for `present=False` or `length=0` to identify missing/empty header issues.
  - `ListTasks completed` — logged on every GET, includes `userId`, `limit`, `count`, and `elapsedMs`. High `elapsedMs` values indicate query performance problems.
- **Stack traces:** Unhandled exceptions are logged by Kestrel with `fail: Microsoft.AspNetCore.Server.Kestrel`. The stack trace includes the file and line number.
- **EF Core SQL log:** When verbose logging is enabled, EF logs the generated SQL for each query. Check for missing `WHERE` or `LIMIT` clauses.
- **Log artifacts:** Production log samples are stored in `artifacts/` for reference during investigations.
- **Swagger (`/swagger`):** Use to quickly test endpoints interactively. Useful for reproducing issues — e.g. Swagger doesn't send custom headers, so `POST /api/tasks` via Swagger reproduces the missing `X-Client-Timestamp` scenario from Report 4.

### What metrics help
- HTTP 500 rate on `POST /api/tasks` — spikes indicate unhandled exceptions
- Response time (p95/p99) on `GET /api/tasks` — sustained increases suggest dataset growth or query regression
- Request volume by client/user-agent — helps identify non-browser API consumers

---

## Diagnosed issues

### Issue 1: Create task 500s (Reports 1 & 4)

**Symptom:** Intermittent 500 errors on `POST /api/tasks`. Users report “sometimes it errors, retry works.”

**Root cause:** `TaskEndpoints.cs:41` calls `DateTime.Parse(clientTimestamp)` on the `X-Client-Timestamp` header **before** input validation. When the header is missing or empty, `DateTime.Parse(“”)` throws `FormatException` → unhandled → 500.

**Two triggers identified:**
- **Local (Report 1):** `main.js:59` randomly sends an empty header ~35% of the time (`Math.random() > 0.35`)
- **Production (Report 4):** Header entirely absent — likely a different client or infrastructure stripping the header

**Diagnosed using log artifact** (`artifacts/sample_api_log.txt`): `present=False length=0` confirmed header was missing, stack trace pinpointed `DateTime.Parse` at `TaskEndpoints.cs:line 44`, payload was valid.

**Fix:** Replace `DateTime.Parse` with `DateTime.TryParse` + `UtcNow` fallback. Remove random empty-header logic in `main.js`.

**Severity:** High — user-facing errors on valid requests.

### Issue 2: Slow task lists (Report 2)

**Symptom:** “My task list takes a long time to load. My coworker says it's fine.”

**Root cause:** `TaskEndpoints.cs:17` calls `db.Tasks.AsNoTracking().ToListAsync()` which loads the **entire Tasks table** into memory, then filters by `userId` and applies `limit` in C# (lines 19–23). The SQL has no `WHERE` or `LIMIT` clause.

**Diagnosed using log artifact** (`artifacts/sample_slow_list_log.txt`): `limit=200` showed 1847ms. EF Core SQL log confirms full table scan with no `WHERE` or `LIMIT`. No indexes on `UserId` or `CreatedAt`.

**Fix:** Moved `Where`, `OrderByDescending`, and `Take` before `ToListAsync` so EF Core generates SQL with `WHERE`, `ORDER BY`, and `LIMIT` clauses. A composite index on `(UserId, CreatedAt)` is not yet added — see `TICKET.md`.

**Severity:** Medium — not a current issue, but degrades with data growth. The query fix prevents full table scans; the index follow-up will ensure performance at larger scale.

### Issue 3: Duplicates and ordering weirdness (Report 3)

**Symptom:** “After I refresh, some tasks show up twice. Also the order looks random sometimes.”

**Root cause:** `main.js:46-47`:
```javascript
state.tasks = state.tasks.concat(items)
  .sort((a, b) => String(a.createdAt).localeCompare(String(b.createdAt)));
```
1. **Duplicates:** `concat(items)` appends fetched tasks to existing state. Every refresh duplicates all tasks.
2. **Ordering:** `localeCompare` sorts ascending (oldest first), contradicting the server's `OrderByDescending` (newest first). Users expect newest at the top.

**Fix:** Replace `concat` with direct assignment. Sort newest-first using `new Date(b.createdAt) - new Date(a.createdAt)`.

**Severity:** Medium — no data corruption but confusing UX. Also contributes to perceived slowness (rendering hundreds of duplicate DOM rows).

---

## Troubleshooting checklist

### “Create task fails with 500”
1. Check server logs for `CreateTask request` line — note `X-Client-Timestamp present` and `length` values
2. Look for `FormatException` or other unhandled exception in the stack trace
3. Verify the request payload has valid `userId` and `title` (rules out 400-class issues)
4. Reproduce via Swagger (`/swagger`): open `POST /api/tasks`, submit with valid `userId` and `title`. Swagger does not send the `X-Client-Timestamp` header, so this directly reproduces the production scenario (Report 4) where the header is absent. If this returns 500, the `DateTime.Parse` fix has not been applied.
5. For header-specific cases, see curl commands in the Verification steps below

### “Tasks list is slow”
1. Check `ListTasks completed` log lines — note `elapsedMs`, `userId`, `limit`, `count`
2. Run `sqlite3 app.db “SELECT COUNT(*) FROM Tasks;”` to confirm dataset size
3. Check EF Core SQL log for the generated query:
   - If SQL lacks `WHERE`, `ORDER BY`, or `LIMIT` → the query-shape fix has regressed (filtering is happening in-memory)
   - If SQL has those clauses but latency is still high → check whether the `(UserId, CreatedAt)` index exists: `sqlite3 app.db “.indexes Tasks”`. If missing, the database is scanning all rows to satisfy the query. See `TICKET.md` for the index follow-up.

### “Duplicates / wrong order after refresh”
1. Open browser DevTools → Network tab
2. Click Refresh, inspect the `GET /api/tasks` response — confirm no duplicates in the JSON
3. Check the DOM: `document.querySelectorAll(“#tbody tr”).length` — if this exceeds the API response count, the client is accumulating
4. Click Refresh again — if the DOM count doubles, the `concat` bug is present
5. Check sort order — if oldest tasks appear first, the ascending sort bug is present
6. **Note:** Seeded task IDs will appear non-sequential when sorted by `CreatedAt` — this is expected. The seeder assigns random `CreatedAt` values within a 2-week window, but IDs are auto-incremented by insertion order. Users may perceive this as "random" even when the sort is working correctly.

---

## Verification steps

### After applying fixes

**Report 1 & 4 (Create task 500s):**

Via curl (for header-specific testing):
- [ ] `curl -X POST http://localhost:5088/api/tasks -H “Content-Type: application/json” -d '{“userId”:”user-001”,”title”:”test”}'` → 201 (no timestamp header)
- [ ] `curl -X POST http://localhost:5088/api/tasks -H “Content-Type: application/json” -H “X-Client-Timestamp: “ -d '{“userId”:”user-001”,”title”:”test”}'` → 201 (empty header)
- [ ] `curl -X POST http://localhost:5088/api/tasks -H “Content-Type: application/json” -H “X-Client-Timestamp: not-a-date” -d '{“userId”:”user-001”,”title”:”test”}'` → 201 (invalid header)
- [ ] `curl -X POST http://localhost:5088/api/tasks -H “Content-Type: application/json” -H “X-Client-Timestamp: 2026-05-21T12:00:00Z” -d '{“userId”:”user-001”,”title”:”test”}'` → 201 (valid header)

Via Swagger (`/swagger`):
- [ ] Open `POST /api/tasks`, submit with `{“userId”:”user-001”,”title”:”test”}` → 201
- [ ] Submit with `{“userId”:””,”title”:”test”}` → 400
- [ ] Submit with `{“userId”:”user-001”,”title”:””}` → 400

Via UI:
- [ ] Click Add in the UI 20+ times rapidly → no 500 errors in server log

Via tests:
- [ ] `dotnet test` → all 12 tests pass

**Report 2 (Slow lists):**

Via curl or Swagger:
- [ ] `curl “http://localhost:5088/api/tasks?userId=user-001&limit=50”` returns only user-001's tasks
- [ ] `curl “http://localhost:5088/api/tasks?userId=user-001&limit=200”` returns at most 200 tasks
- [ ] Swagger: `GET /api/tasks` with `userId=user-001` and `limit=50` → 200, verify response contains only user-001's tasks

Via logs:
- [ ] EF Core SQL log includes `WHERE` and `LIMIT` clauses
- [ ] `ListTasks completed` log shows reduced `elapsedMs`

**Report 3 (Duplicates / ordering):**

Via UI:
- [ ] Load page, open DevTools console, run `document.querySelectorAll(“#tbody tr”).length` → should be 50
- [ ] Click Refresh 5 times, rerun the same query → should still be 50
- [ ] Add a new task → it appears at the top of the list
- [ ] All tasks are ordered newest-first
- [ ] Switch users via dropdown → list resets correctly with no carryover

---

## Rollback / mitigation

### If the fix introduces regressions

**Report 1 & 4 fix (TryParse fallback):**
- Revert `TaskEndpoints.cs` to use `DateTime.Parse` (restores original behavior)
- As a temporary mitigation without reverting: add a global exception handler that catches `FormatException` and returns 400 instead of 500
- Risk of revert: 500s return, but no data corruption

**Report 2 fix (DB-side filtering):**
- Revert the query in `TaskEndpoints.cs` to load all rows + filter in-memory
- Risk of revert: performance returns to previous levels but no correctness issue
- If the new query produces incorrect results, this is the safest rollback

**Report 3 fix (client-side state):**
- Revert `main.js` to use `concat` + `localeCompare`
- Risk of revert: duplicates and ordering issues return, but no server impact
- This is a client-only change — users get the fix immediately on page reload, no deployment pipeline needed for static assets

### General rollback procedure
```bash
# Check current state
git log --oneline -5
git diff HEAD~1

# Revert last commit
git revert HEAD

# Or reset to specific known-good commit
git checkout <commit-hash> -- src/SupportEngineerChallenge.Api/Endpoints/TaskEndpoints.cs
git checkout <commit-hash> -- src/SupportEngineerChallenge.Api/wwwroot/main.js
```

