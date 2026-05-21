# Follow-up Ticket

**Title:** Add composite index and performance guardrails for task list query
**Priority:** P2
**Severity rationale:** Not P1 because the query fix (DB-side filtering) already eliminates full table scans and response times are acceptable at the current dataset size. P2 because without an index, query performance will degrade as the dataset grows — the `WHERE` and `ORDER BY` clauses now hit the database but have no supporting index. Not P3 because this is a predictable scaling issue on a core workflow.
**Owner:** Backend team

## Description

The `GET /api/tasks` query was fixed to push `Where`, `OrderByDescending`, and `Take` to the database (no longer loads the full table into memory). However, there is no composite index on `(UserId, CreatedAt)`, so the database must still scan all rows to satisfy the `WHERE` and `ORDER BY` clauses. At the current dataset size (~60k rows) this is fast enough, but will degrade as the table grows.

Additionally, there are no performance guardrails to detect regressions — response time is logged but not monitored or alerted on.

## Acceptance criteria

- [ ] Add a composite index on `Tasks(UserId, CreatedAt)` via EF Core migration or `OnModelCreating`
- [ ] Verify via `sqlite3 app.db ".indexes Tasks"` that the index exists after migration
- [ ] Response time for `limit=200` stays under 50ms at 100k+ rows
- [ ] Add an alert or monitoring threshold if `ListTasks completed` `elapsedMs` exceeds 500ms
- [ ] Existing tests still pass (`dotnet test` — 12/12)
- [ ] No changes to the API contract

## Notes / context

- **Related incident:** See `INCIDENT.md` — Report 2. The query was fixed during incident response. This ticket covers the remaining index and monitoring work.
- **Files to change:** `src/SupportEngineerChallenge.Api/Data/AppDbContext.cs` (add index in `OnModelCreating`), optionally add an EF Core migration
- **Current state:** The query generates proper `WHERE`, `ORDER BY`, and `LIMIT` clauses. Performance is acceptable now but has no index safety net for growth.
- **Risk:** Low — adding a read-optimized index has no impact on correctness. Minor write overhead on inserts.
- **Monitoring:** After adding the index, compare `elapsedMs` in `ListTasks completed` logs before and after. Set up an alert if p95 exceeds 500ms.
