using System.Text.Json;

namespace ClaudeUsageWidget.Tests;

internal static class TestUtil
{
    // Build one transcript (.jsonl) line as Claude Code writes it.
    public static string Line(string id, string model, string tsIso, long inp, long outp, long cr, long cw)
    {
        return JsonSerializer.Serialize(new
        {
            timestamp = tsIso,
            requestId = "req_" + id,
            type = "assistant",
            message = new
            {
                id = id,
                model = model,
                role = "assistant",
                usage = new
                {
                    input_tokens = inp,
                    output_tokens = outp,
                    cache_read_input_tokens = cr,
                    cache_creation_input_tokens = cw
                }
            }
        });
    }

    public static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cuw-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void WriteJsonl(string dir, string name, string[] lines, DateTime mtimeLocal)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllLines(path, lines);
        File.SetLastWriteTime(path, mtimeLocal);
    }

    public static long Ms(DateTime local)
    {
        return new DateTimeOffset(local).ToUnixTimeMilliseconds();
    }

    // One line of ~/.claude/history.jsonl as Claude Code writes it.
    public static string HistoryLine(string sessionId, string project, string display, DateTime whenLocal)
    {
        return JsonSerializer.Serialize(new
        {
            display = display,
            pastedContents = new { },
            timestamp = Ms(whenLocal),
            project = project,
            sessionId = sessionId
        });
    }

    // One ~/.claude/sessions/<pid>.json file.
    public static void WriteSessionFile(string dir, int pid, string sessionId, string cwd, string status, DateTime updatedLocal, string name = "cli")
    {
        string json = JsonSerializer.Serialize(new
        {
            pid = pid,
            sessionId = sessionId,
            cwd = cwd,
            startedAt = Ms(updatedLocal.AddHours(-1)),
            version = "2.1.220",
            kind = "interactive",
            entrypoint = "cli",
            name = name,
            updatedAt = Ms(updatedLocal),
            status = status
        });
        File.WriteAllText(Path.Combine(dir, pid + ".json"), json);
    }

    public static string Uuid(int seed)
    {
        return new Guid(seed, 0, 0, new byte[8]).ToString();
    }
}
