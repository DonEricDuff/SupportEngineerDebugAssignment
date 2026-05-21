# Follow-up Ticket

**Title:** Optimize task list query — push filtering and pagination to the database
**Priority:** P2
**Severity rationale:** Not P1 because the endpoint is functional and returns correct data. P2 because production users are experiencing up to 1.8s response times on a core workflow, and this will degrade further as the dataset grows. Not P3 because the impact is already user-noticeable in production.
**Owner:** Backend team

## Description

The `GET /api/tasks` endpoint (`TaskEndpoints.cs:17-23`) loads the entire `Tasks` table into memory on every request, then filters by `userId` and applies `limit` in C#. The generated SQL has no `WHERE` or `LIMIT` clause.

Production logs (`artifacts/sample_slow_list_log.txt`) show response times up to 1.8s for `limit=200`. This will degrade further as the dataset grows.

## Acceptance criteria

- [ ] `GET /api/tasks?userId=X&limit=N` generates SQL with `WHERE`, `ORDER BY`, and `LIMIT` clauses
- [ ] The EF Core query log confirms no full table scan: `SELECT ... FROM "Tasks" WHERE "UserId" = @p0 ORDER BY "CreatedAt" DESC LIMIT @p1`
- [ ] Response time for `limit=50` is under 20ms at 60k rows (currently ~300ms)
- [ ] Response time for `limit=200` is under 50ms at 60k rows (currently ~1.8s)
- [ ] Existing test `ListTasks_ShouldReturnOnlyRequestedUser` still passes
- [ ] Add a test verifying the `limit` parameter is respected (returns at most N results)
- [ ] Add a test verifying results are ordered by `CreatedAt` descending
- [ ] No changes to the API contract (same request/response shape)
- [ ] Verify via Swagger (`/swagger`): `GET /api/tasks` with different `userId` and `limit` values returns correct, filtered results

## Notes / context

- **Suggested approach:** Move the `Where`, `OrderByDescending`, and `Take` calls before `ToListAsync` so EF Core translates them to SQL. Optionally add a composite index on `(UserId, CreatedAt)` for larger datasets.

- **Related incident:** See `INCIDENT.md` — this was Report 2, diagnosed but not fixed during incident response. Priority was given to the 500 errors (Reports 1 & 4) and UI bugs (Report 3).
- **Files to change:** `src/SupportEngineerChallenge.Api/Endpoints/TaskEndpoints.cs` (lines 17-23), optionally `Data/AppDbContext.cs` for index
- **Risk:** Needs investigation. The logic is the same (filter by userId, order by createdAt, take N), but moving it from C# to SQL changes what the database returns. Other code or consumers may depend on the full result set. Audit usages of the `GET /api/tasks` endpoint and the `Tasks` DbSet before applying. The existing in-memory behavior is the fallback if reverted.
- **Monitoring:** After deployment, check `ListTasks completed` log lines — `elapsedMs` should drop significantly. Consider adding an alert if `elapsedMs` exceeds 500ms.

