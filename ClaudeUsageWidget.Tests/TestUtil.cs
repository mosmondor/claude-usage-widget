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
}
