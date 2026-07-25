using ClaudeUsageWidget.Core;
using Xunit;

namespace ClaudeUsageWidget.Tests;

public class SessionNamesTests
{
    // the two shapes Claude Code actually writes into a transcript
    private const string RenameLine =
        "{\"type\":\"user\",\"subtype\":\"local_command\",\"content\":\"<local-command-stdout>Session renamed to: playlist-generation</local-command-stdout>\",\"level\":\"info\"}";
    private const string NamedLine =
        "{\"message\":{\"role\":\"user\",\"content\":\"<system-reminder>\\nThe user named this session \\\"fireplay-hub\\\". This may indicate the session's focus.\\n</system-reminder>\"}}";

    [Fact]
    public void ExtractsBothFormsClaudeCodeWrites()
    {
        Assert.Equal("playlist-generation", SessionNames.ExtractFromLine(RenameLine));
        Assert.Equal("fireplay-hub", SessionNames.ExtractFromLine(NamedLine));
    }

    [Fact]
    public void OrdinaryLinesYieldNothing()
    {
        Assert.Equal("", SessionNames.ExtractFromLine("{\"type\":\"assistant\",\"message\":{\"id\":\"x\"}}"));
        Assert.Equal("", SessionNames.ExtractFromLine(""));
        Assert.Equal("", SessionNames.ExtractFromLine(null));
    }

    [Fact]
    public void LastRenameWins()
    {
        string dir = TestUtil.NewTempDir();
        string path = Path.Combine(dir, "s.jsonl");
        File.WriteAllLines(path, new string[]
        {
            RenameLine,
            "{\"type\":\"assistant\"}",
            RenameLine.Replace("playlist-generation", "playlist-generation-v2")
        });

        Assert.Equal("playlist-generation-v2", SessionNames.ScanFile(path));
    }

    [Fact]
    public void UnnamedTranscriptGivesEmpty()
    {
        string dir = TestUtil.NewTempDir();
        string path = Path.Combine(dir, "s.jsonl");
        File.WriteAllLines(path, new string[] { "{\"type\":\"assistant\"}", "{\"type\":\"user\"}" });

        Assert.Equal("", SessionNames.ScanFile(path));
    }

    [Fact]
    public void ResolveFindsNamesBySessionIdAndCachesThem()
    {
        string projects = TestUtil.NewTempDir();
        string proj = Path.Combine(projects, "C--Projects-fireplay");
        Directory.CreateDirectory(proj);
        string sid = TestUtil.Uuid(42);
        string other = TestUtil.Uuid(43);
        File.WriteAllLines(Path.Combine(proj, sid + ".jsonl"), new string[] { RenameLine });
        File.WriteAllLines(Path.Combine(proj, other + ".jsonl"), new string[] { "{\"type\":\"assistant\"}" });

        string cache = Path.Combine(TestUtil.NewTempDir(), "names.json");
        SessionNames names = new SessionNames(projects, cache);
        Dictionary<string, string> got = names.Resolve(new string[] { sid, other, TestUtil.Uuid(99) });

        Assert.Single(got);                       // only the named one is reported
        Assert.Equal("playlist-generation", got[sid]);
        Assert.True(File.Exists(cache));

        // a second instance reads the cache; deleting the transcript proves it was not re-scanned
        File.Delete(Path.Combine(proj, sid + ".jsonl"));
        SessionNames again = new SessionNames(projects, cache);
        Assert.Empty(again.Resolve(new string[] { sid }));   // file gone -> nothing to report
    }

    [Fact]
    public void RescansWhenTheTranscriptChanged()
    {
        string projects = TestUtil.NewTempDir();
        string proj = Path.Combine(projects, "p");
        Directory.CreateDirectory(proj);
        string sid = TestUtil.Uuid(50);
        string path = Path.Combine(proj, sid + ".jsonl");
        File.WriteAllLines(path, new string[] { RenameLine });

        string cache = Path.Combine(TestUtil.NewTempDir(), "names.json");
        SessionNames names = new SessionNames(projects, cache);
        Assert.Equal("playlist-generation", names.Resolve(new string[] { sid })[sid]);

        File.WriteAllLines(path, new string[] { RenameLine.Replace("playlist-generation", "renamed-later") });
        Assert.Equal("renamed-later", names.Resolve(new string[] { sid })[sid]);
    }

    [Fact]
    public void NestedFoldersAreNotMistakenForTranscripts()
    {
        // project folders contain subagents/ and tool-results/ subfolders
        string projects = TestUtil.NewTempDir();
        string proj = Path.Combine(projects, "p");
        string nested = Path.Combine(proj, "some-session", "subagents");
        Directory.CreateDirectory(nested);
        string sid = TestUtil.Uuid(60);
        File.WriteAllLines(Path.Combine(nested, sid + ".jsonl"), new string[] { RenameLine });

        SessionNames names = new SessionNames(projects, Path.Combine(TestUtil.NewTempDir(), "n.json"));
        Assert.Empty(names.Resolve(new string[] { sid }));
    }

    [Fact]
    public void LabelPrefersTheNameAndFallsBackToTheTopic()
    {
        SessionEntry e = new SessionEntry();
        e.FirstPrompt = "deployaj fireplay-hub";
        Assert.False(e.HasName);
        Assert.Equal("deployaj fireplay-hub", e.Label);

        e.Name = "fireplay-hub";
        Assert.True(e.HasName);
        Assert.Equal("fireplay-hub", e.Label);

        SessionEntry blank = new SessionEntry();
        Assert.Equal("(unnamed)", blank.Label);
    }
}
