using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SupportEngineerChallenge.Tests;

public class TaskApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public TaskApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateTask_ShouldReturn201_WhenValid()
    {
        var client = _factory.CreateClient();

        var req = new { userId = "user-001", title = "Test task" };

        client.DefaultRequestHeaders.Add("X-Client-Timestamp", DateTime.UtcNow.ToString("O"));

        var res = await client.PostAsJsonAsync("/api/tasks", req);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ListTasks_ShouldReturnOnlyRequestedUser()
    {
        var client = _factory.CreateClient();

        var user1 = await client.GetFromJsonAsync<List<TaskDto>>("/api/tasks?userId=user-001&limit=50");
        user1.Should().NotBeNull();
        user1!.Should().OnlyContain(t => t.UserId == "user-001");
    }

    [Fact]
    public async Task CreateTask_ShouldReturn201_WhenTimestampMissing()
    {
        var client = _factory.CreateClient();

        var req = new { userId = "user-001", title = "No timestamp task" };

        // No X-Client-Timestamp header at all — reproduces Report 4 (production 500)
        var res = await client.PostAsJsonAsync("/api/tasks", req);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateTask_ShouldReturn201_WhenTimestampEmpty()
    {
        var client = _factory.CreateClient();

        var req = new { userId = "user-001", title = "Empty timestamp task" };

        // Empty X-Client-Timestamp header — reproduces Report 1 (intermittent 500)
        client.DefaultRequestHeaders.Add("X-Client-Timestamp", "");

        var res = await client.PostAsJsonAsync("/api/tasks", req);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateTask_ShouldReturn201_WhenTimestampInvalid()
    {
        var client = _factory.CreateClient();

        var req = new { userId = "user-001", title = "Bad timestamp task" };

        client.DefaultRequestHeaders.Add("X-Client-Timestamp", "not-a-date");

        var res = await client.PostAsJsonAsync("/api/tasks", req);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateTask_ShouldReturn400_WhenUserIdMissing()
    {
        var client = _factory.CreateClient();

        var req = new { userId = "", title = "Missing user" };

        client.DefaultRequestHeaders.Add("X-Client-Timestamp", DateTime.UtcNow.ToString("O"));

        var res = await client.PostAsJsonAsync("/api/tasks", req);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTask_ShouldReturn400_WhenTitleMissing()
    {
        var client = _factory.CreateClient();

        var req = new { userId = "user-001", title = "" };

        client.DefaultRequestHeaders.Add("X-Client-Timestamp", DateTime.UtcNow.ToString("O"));

        var res = await client.PostAsJsonAsync("/api/tasks", req);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTask_ShouldUseProvidedTimestamp_WhenValid()
    {
        var client = _factory.CreateClient();

        var timestamp = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var req = new { userId = "user-001", title = "Timestamped task" };

        client.DefaultRequestHeaders.Add("X-Client-Timestamp", timestamp.ToString("O"));

        var res = await client.PostAsJsonAsync("/api/tasks", req);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await res.Content.ReadFromJsonAsync<TaskDto>();
        created.Should().NotBeNull();
        // DateTime.Parse may convert to local time, so compare as UTC
        created!.CreatedAt.ToUniversalTime().Should().BeCloseTo(timestamp, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ListTasks_ShouldReturnNoDuplicates()
    {
        var client = _factory.CreateClient();

        var tasks = await client.GetFromJsonAsync<List<TaskDto>>("/api/tasks?userId=user-001&limit=50");
        tasks.Should().NotBeNull();

        var ids = tasks!.Select(t => t.Id).ToList();
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ListTasks_ShouldReturnOrderedByCreatedAtDescending()
    {
        var client = _factory.CreateClient();

        var tasks = await client.GetFromJsonAsync<List<TaskDto>>("/api/tasks?userId=user-001&limit=50");
        tasks.Should().NotBeNull();
        tasks!.Should().HaveCountGreaterThan(1);

        // Verify newest-first ordering
        tasks.Should().BeInDescendingOrder(t => t.CreatedAt);
    }

    public record TaskDto(int Id, string UserId, string Title, string Status, DateTime CreatedAt, DateTime UpdatedAt);
}
