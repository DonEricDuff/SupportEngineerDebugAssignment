using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SupportEngineerChallenge.Api.Data;
using SupportEngineerChallenge.Api.Models;

namespace SupportEngineerChallenge.Api.Endpoints;

public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/tasks");

        group.MapGet("", async (string userId, int? limit, AppDbContext db, ILogger<Program> logger) =>
        {
            var sw = Stopwatch.StartNew();
            var all = await db.Tasks.AsNoTracking().ToListAsync();

            var filtered = all
                .Where(t => t.UserId == userId)
                .OrderByDescending(t => t.CreatedAt)
                .Take(Math.Clamp(limit ?? 50, 1, 200))
                .ToList();

            sw.Stop();
            logger.LogInformation(
                "ListTasks completed userId={UserId} limit={Limit} count={Count} elapsedMs={ElapsedMs}",
                userId, limit ?? 50, filtered.Count, sw.ElapsedMilliseconds);

            return Results.Ok(filtered);
        });

        group.MapPost("", async (HttpContext ctx, CreateTaskRequest req, AppDbContext db, ILogger<Program> logger) =>
        {
            var clientTimestamp = ctx.Request.Headers["X-Client-Timestamp"].ToString();
            var hasTimestamp = !string.IsNullOrWhiteSpace(clientTimestamp);
            logger.LogInformation(
                "CreateTask request UserId={UserId} Title={Title} X-Client-Timestamp present={HasTimestamp} length={Length}",
                req?.UserId ?? "(null)", req?.Title ?? "(null)", hasTimestamp, clientTimestamp?.Length ?? 0);

            if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { message = "userId and title are required" });

            if (!DateTime.TryParse(clientTimestamp, out var createdAt))
                createdAt = DateTime.UtcNow;

            var task = new TaskItem
            {
                UserId = req.UserId,
                Title = req.Title.Trim(),
                Status = "open",
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            };

            db.Tasks.Add(task);
            await db.SaveChangesAsync();

            return Results.Created($"/api/tasks/{task.Id}", task);
        });
    }
}

public record CreateTaskRequest(string UserId, string Title);
