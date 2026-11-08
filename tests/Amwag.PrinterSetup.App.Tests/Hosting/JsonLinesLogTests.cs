using System.ComponentModel;
using System.Text.Json;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Tests.Hosting;

public sealed class JsonLinesLogTests : IDisposable
{
    private const string Session = "0123456789ab";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "amwag-apphost-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void Each_entry_is_one_json_object_on_its_own_line()
    {
        string path;
        using (var log = new JsonLinesLog(_directory, "worker", Session))
        {
            log.Info("detect.snapshot", "Snapshot taken.", new Dictionary<string, object?>
            {
                ["queues"] = 3,
                ["names"] = new[] { "A", "B" },
                ["ratio"] = 0.5,
                ["code"] = FindingCode.DriverMissing,
                ["nested"] = new Dictionary<string, object?> { ["ok"] = true, ["missing"] = null },
                ["multiline"] = "line 1\r\nline 2",
            });
            log.Error("apply.action.failed", "AddForm failed.", new PrinterSetupException(FindingCode.FormCreateFailed, "AddForm failed (Win32 error 5).", 5));
            log.Warn("app.task.unobserved", "Warned.", exception: new Win32Exception(1223));
            path = log.FilePath!;
        }

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);

        using var first = JsonDocument.Parse(lines[0]);
        JsonElement entry = first.RootElement;
        Assert.True(DateTimeOffset.TryParse(entry.GetProperty("ts").GetString(), out _));
        Assert.Matches(@"[+-]\d{2}:\d{2}$|Z$", entry.GetProperty("ts").GetString()!);
        Assert.Equal("info", entry.GetProperty("level").GetString());
        Assert.Equal(Session, entry.GetProperty("session").GetString());
        Assert.Equal("worker", entry.GetProperty("role").GetString());
        Assert.Equal(Environment.ProcessId, entry.GetProperty("pid").GetInt32());
        Assert.Equal("detect.snapshot", entry.GetProperty("event").GetString());
        Assert.Equal("Snapshot taken.", entry.GetProperty("message").GetString());
        JsonElement data = entry.GetProperty("data");
        Assert.Equal(3, data.GetProperty("queues").GetInt32());
        Assert.Equal(new[] { "A", "B" }, data.GetProperty("names").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(0.5, data.GetProperty("ratio").GetDouble());
        Assert.Equal("driverMissing", data.GetProperty("code").GetString());
        Assert.True(data.GetProperty("nested").GetProperty("ok").GetBoolean());
        Assert.Equal("line 1\r\nline 2", data.GetProperty("multiline").GetString());
        Assert.False(entry.TryGetProperty("exception", out _));

        using var second = JsonDocument.Parse(lines[1]);
        JsonElement exception = second.RootElement.GetProperty("exception");
        Assert.Equal("error", second.RootElement.GetProperty("level").GetString());
        Assert.Equal(typeof(PrinterSetupException).FullName, exception.GetProperty("type").GetString());
        Assert.Equal("formCreateFailed", exception.GetProperty("code").GetString());
        Assert.Equal(5, exception.GetProperty("win32Error").GetInt32());
        Assert.Contains("AddForm failed", exception.GetProperty("detail").GetString());

        using var third = JsonDocument.Parse(lines[2]);
        Assert.Equal("warning", third.RootElement.GetProperty("level").GetString());
        Assert.Equal(1223, third.RootElement.GetProperty("exception").GetProperty("win32Error").GetInt32());
    }

    [Fact]
    public void The_file_name_carries_time_role_session_and_process()
    {
        using var log = new JsonLinesLog(_directory, "interactive", Session);

        Assert.Equal(_directory, Path.GetDirectoryName(log.FilePath));
        Assert.Matches($@"^\d{{8}}-\d{{6}}-interactive-{Session}-{Environment.ProcessId}\.jsonl$", Path.GetFileName(log.FilePath!));
        Assert.Equal(Session, log.SessionId);
    }

    [Fact]
    public void Data_that_cannot_be_serialized_is_written_as_text()
    {
        var cycle = new Node();
        cycle.Next = cycle;
        string path;
        using (var log = new JsonLinesLog(_directory, "silent", Session))
        {
            log.Info("odd.data", "Odd values.", new Dictionary<string, object?> { ["type"] = typeof(string), ["cycle"] = cycle });
            path = log.FilePath!;
        }

        using var entry = JsonDocument.Parse(File.ReadAllLines(path).Single());
        JsonElement data = entry.RootElement.GetProperty("data");
        Assert.Equal(typeof(string).ToString(), data.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.String, data.GetProperty("cycle").ValueKind);
    }

    [Fact]
    public void Concurrent_writes_produce_whole_lines()
    {
        string path;
        using (var log = new JsonLinesLog(_directory, "interactive", Session))
        {
            Parallel.For(0, 400, i => log.Debug("parallel.entry", "Entry " + i, new Dictionary<string, object?> { ["i"] = i }));
            path = log.FilePath!;
        }

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(400, lines.Length);
        var numbers = lines.Select(l =>
        {
            using var document = JsonDocument.Parse(l);
            return document.RootElement.GetProperty("data").GetProperty("i").GetInt32();
        }).ToHashSet();
        Assert.Equal(400, numbers.Count);
    }

    [Fact]
    public void Writing_after_dispose_is_ignored()
    {
        var log = new JsonLinesLog(_directory, "worker", Session);
        string path = log.FilePath!;
        log.Dispose();

        log.Info("late.entry", "Too late.");
        log.Dispose();

        Assert.Empty(File.ReadAllLines(path));
    }

    [Fact]
    public void An_unusable_folder_gives_a_log_that_writes_nothing()
    {
        Directory.CreateDirectory(_directory);
        string blocker = Path.Combine(_directory, "not-a-folder");
        File.WriteAllText(blocker, "");

        using var log = new JsonLinesLog(Path.Combine(blocker, "Logs"), "worker", Session);
        log.Info("nowhere", "Nothing happens.");

        Assert.Null(log.FilePath);
    }

    [Fact]
    public void Opening_deletes_only_own_logs_older_than_60_days()
    {
        Directory.CreateDirectory(_directory);
        string oldOwn = Touch("20250101-080000-worker-0123456789ab-4242.jsonl", DateTime.UtcNow.AddDays(-61));
        string recentOwn = Touch("20261001-080000-interactive-0123456789ab-4242.jsonl", DateTime.UtcNow.AddDays(-59));
        string oldForeign = Touch("someone-else.jsonl", DateTime.UtcNow.AddDays(-400));
        string oldText = Touch("20250101-080000-worker-0123456789ab-4242.txt", DateTime.UtcNow.AddDays(-400));

        using var log = new JsonLinesLog(_directory, "worker", Session);

        Assert.False(File.Exists(oldOwn));
        Assert.True(File.Exists(recentOwn));
        Assert.True(File.Exists(oldForeign));
        Assert.True(File.Exists(oldText));
        Assert.True(File.Exists(log.FilePath));
    }

    private string Touch(string name, DateTime lastWriteUtc)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, "{}\r\n");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private sealed class Node
    {
        public Node? Next { get; set; }

        public override string ToString() => "node";
    }
}
