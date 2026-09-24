using System.Text.Json.Nodes;
using BD.Connectors.ClaudeCode.Internal;
using BD.Connectors.ClaudeCode.Sessions;

namespace BD.Connectors.ClaudeCode.Tests.Unit;

// TranscriptMirrorBatcher, ported from PY.
[Property("TestKind", "Unit")]
public class TranscriptMirrorBatcherTests
{
    // Records every AppendAsync call; can be configured to fail the first N calls per key before
    // succeeding, or to always fail.
    private sealed class RecordingStore : ISessionStore
    {
        public List<(SessionKey Key, List<JsonObject> Entries)> Calls { get; } = [];

        public int FailuresBeforeSuccess { get; set; }

        public bool AlwaysFail { get; set; }

        private int _attempts;

        public Task AppendAsync(SessionKey key, IReadOnlyList<JsonObject> entries, CancellationToken cancellationToken = default)
        {
            Calls.Add((key, [.. entries]));
            _attempts++;
            if (AlwaysFail || _attempts <= FailuresBeforeSuccess)
            {
                throw new InvalidOperationException("simulated adapter failure");
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<JsonObject>?> LoadAsync(SessionKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JsonObject>?>(null);
    }

    private static JsonArray Entries(params string[] uuids) =>
        new([.. uuids.Select(u => (JsonNode?)new JsonObject { ["type"] = "user", ["uuid"] = u })]);

    private static string ProjectsDir => Path.Combine("C:", "claude-test", "projects");

    private static string FilePath(string project, string session) => Path.Combine(ProjectsDir, project, $"{session}.jsonl");

    [Test]
    public async Task FlushAsync_AppendsEnqueuedEntries_WithCorrectSessionKey()
    {
        var store = new RecordingStore();
        var errors = new List<(SessionKey? Key, string Message)>();
        var batcher = new TranscriptMirrorBatcher(store, ProjectsDir, (k, m) => { errors.Add((k, m)); return Task.CompletedTask; });

        batcher.Enqueue(FilePath("proj-a", "session-1"), Entries("u1", "u2"));
        await batcher.FlushAsync();

        await Assert.That(store.Calls.Count).IsEqualTo(1);
        await Assert.That(store.Calls[0].Key).IsEqualTo(new SessionKey("proj-a", "session-1"));
        await Assert.That(store.Calls[0].Entries.Count).IsEqualTo(2);
        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Enqueue_MultipleFramesSameFile_CoalescedIntoOneAppendCall()
    {
        var store = new RecordingStore();
        var batcher = new TranscriptMirrorBatcher(store, ProjectsDir, (_, _) => Task.CompletedTask);

        batcher.Enqueue(FilePath("proj-a", "session-1"), Entries("u1"));
        batcher.Enqueue(FilePath("proj-a", "session-1"), Entries("u2"));
        batcher.Enqueue(FilePath("proj-a", "session-1"), Entries("u3"));
        await batcher.FlushAsync();

        await Assert.That(store.Calls.Count).IsEqualTo(1);
        await Assert.That(string.Join(",", store.Calls[0].Entries.Select(e => e["uuid"]!.ToString()))).IsEqualTo("u1,u2,u3");
    }

    [Test]
    public async Task Enqueue_DifferentFiles_ProduceSeparateAppendCallsInFirstSeenOrder()
    {
        var store = new RecordingStore();
        var batcher = new TranscriptMirrorBatcher(store, ProjectsDir, (_, _) => Task.CompletedTask);

        batcher.Enqueue(FilePath("proj-a", "session-2"), Entries("u2"));
        batcher.Enqueue(FilePath("proj-a", "session-1"), Entries("u1"));
        await batcher.FlushAsync();

        await Assert.That(store.Calls.Count).IsEqualTo(2);
        await Assert.That(store.Calls[0].Key.SessionId).IsEqualTo("session-2");
        await Assert.That(store.Calls[1].Key.SessionId).IsEqualTo("session-1");
    }

    [Test]
    public async Task Enqueue_FilePathNotUnderProjectsDir_DroppedWithoutThrowing()
    {
        var store = new RecordingStore();
        var batcher = new TranscriptMirrorBatcher(store, ProjectsDir, (_, _) => Task.CompletedTask);

        batcher.Enqueue(Path.Combine("C:", "elsewhere", "session-1.jsonl"), Entries("u1"));
        await batcher.FlushAsync();

        await Assert.That(store.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FlushAsync_EmptyPendingBuffer_DoesNotCallStore()
    {
        var store = new RecordingStore();
        var batcher = new TranscriptMirrorBatcher(store, ProjectsDir, (_, _) => Task.CompletedTask);

        await batcher.FlushAsync();

        await Assert.That(store.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Append_RetriesTransientFailure_ThenSucceeds()
    {
        var store = new RecordingStore { FailuresBeforeSuccess = 2 };
        var errors = new List<(SessionKey? Key, string Message)>();
        var batcher = new TranscriptMirrorBatcher(store, ProjectsDir, (k, m) => { errors.Add((k, m)); return Task.CompletedTask; });

        batcher.Enqueue(FilePath("proj-a", "session-1"), Entries("u1"));
        await batcher.FlushAsync();

        // 2 failed attempts + 1 successful attempt = 3 total calls to the adapter.
        await Assert.That(store.Calls.Count).IsEqualTo(3);
        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Append_FailsAllAttempts_ReportsViaOnErrorAfterMaxAttempts()
    {
        var store = new RecordingStore { AlwaysFail = true };
        var errors = new List<(SessionKey? Key, string Message)>();
        var batcher = new TranscriptMirrorBatcher(store, ProjectsDir, (k, m) => { errors.Add((k, m)); return Task.CompletedTask; });

        batcher.Enqueue(FilePath("proj-a", "session-1"), Entries("u1"));
        await batcher.FlushAsync();

        // 3 total attempts (MIRROR_APPEND_MAX_ATTEMPTS).
        await Assert.That(store.Calls.Count).IsEqualTo(3);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Key).IsEqualTo(new SessionKey("proj-a", "session-1"));
        await Assert.That(errors[0].Message).Contains("simulated adapter failure");
    }

    [Test]
    public async Task Enqueue_ExceedingMaxPendingEntries_TriggersEagerBackgroundFlush()
    {
        var store = new RecordingStore();
        var batcher = new TranscriptMirrorBatcher(store, ProjectsDir, (_, _) => Task.CompletedTask, maxPendingEntries: 1, maxPendingBytes: int.MaxValue);

        batcher.Enqueue(FilePath("proj-a", "session-1"), Entries("u1", "u2"));

        // Enqueue schedules the eager flush on a background Task; wait for it explicitly rather than
        // sleeping/polling.
        await (batcher.LastEagerFlushTask ?? Task.CompletedTask);

        await Assert.That(store.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CloseAsync_FlushesPendingEntries_AndNeverThrows()
    {
        var store = new RecordingStore { AlwaysFail = true };
        var batcher = new TranscriptMirrorBatcher(store, ProjectsDir, (_, _) => throw new InvalidOperationException("on_error also fails"));

        batcher.Enqueue(FilePath("proj-a", "session-1"), Entries("u1"));

        await batcher.CloseAsync();

        await Assert.That(store.Calls.Count).IsEqualTo(3);
    }
}
