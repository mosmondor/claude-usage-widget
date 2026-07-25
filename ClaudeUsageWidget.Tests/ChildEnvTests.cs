using ClaudeUsageWidget.Core;
using Xunit;

namespace ClaudeUsageWidget.Tests;

public class ChildEnvTests
{
    // exactly what a widget started from inside a Claude Code session inherits
    private static readonly string[] Inherited =
    {
        "CLAUDECODE",
        "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_BRIDGE_SESSION_ID",
        "CLAUDE_CODE_ENTRYPOINT",
        "CLAUDE_CODE_NO_FLICKER",
        "CLAUDE_PID",
        "PATH",
        "USERPROFILE"
    };

    [Fact]
    public void InheritedMarkersAreRemoved()
    {
        List<string> remove = ChildEnv.NamesToRemove(Inherited, new string[0]);

        Assert.Contains("CLAUDE_CODE_CHILD_SESSION", remove);   // this one turns transcript saving off
        Assert.Contains("CLAUDE_CODE_SESSION_ID", remove);
        Assert.Contains("CLAUDECODE", remove);
        Assert.Contains("CLAUDE_PID", remove);
        Assert.Equal(7, remove.Count);
    }

    [Fact]
    public void UnrelatedVariablesAreLeftAlone()
    {
        List<string> remove = ChildEnv.NamesToRemove(Inherited, new string[0]);

        Assert.DoesNotContain("PATH", remove);
        Assert.DoesNotContain("USERPROFILE", remove);
    }

    [Fact]
    public void UserConfigurationSurvives()
    {
        // set persistently by the user -> configuration, not an inherited marker
        string[] current = { "CLAUDE_CODE_MAX_OUTPUT_TOKENS", "CLAUDE_CODE_CHILD_SESSION" };
        string[] persisted = { "CLAUDE_CODE_MAX_OUTPUT_TOKENS" };

        List<string> remove = ChildEnv.NamesToRemove(current, persisted);

        Assert.Single(remove);
        Assert.Equal("CLAUDE_CODE_CHILD_SESSION", remove[0]);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        List<string> remove = ChildEnv.NamesToRemove(new string[] { "claude_code_child_session" }, new string[0]);
        Assert.Single(remove);

        List<string> kept = ChildEnv.NamesToRemove(new string[] { "Claude_Code_Thing" }, new string[] { "CLAUDE_CODE_THING" });
        Assert.Empty(kept);
    }

    [Fact]
    public void NullsAndBlanksAreHandled()
    {
        Assert.Empty(ChildEnv.NamesToRemove(null, null));
        Assert.Empty(ChildEnv.NamesToRemove(new string[] { "", null }, null));
    }
}
