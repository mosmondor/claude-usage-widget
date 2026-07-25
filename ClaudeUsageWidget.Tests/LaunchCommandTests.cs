using ClaudeUsageWidget.Core;
using Xunit;

namespace ClaudeUsageWidget.Tests;

public class LaunchCommandTests
{
    private const string Sid = "9b36e997-79e4-47e1-86f1-d80fce80546b";

    [Fact]
    public void WithWindowsTerminal_OpensATabInTheExistingWindow()
    {
        (string exe, string args) cmd = LaunchCommand.Resume(
            "C:\\wt.exe", "pwsh.exe", "C:\\Projects\\Informacija\\OtherProjects\\admin", Sid, "admin");

        Assert.Equal("C:\\wt.exe", cmd.exe);
        Assert.Contains("-w 0 nt", cmd.args);                                           // existing window, new tab
        Assert.Contains("-d \"C:\\Projects\\Informacija\\OtherProjects\\admin\"", cmd.args);
        Assert.Contains("--title \"admin\"", cmd.args);
        Assert.Contains("pwsh.exe -NoExit -Command claude -r " + Sid, cmd.args);
    }

    [Fact]
    public void WithoutWindowsTerminal_FallsBackToAPlainShellWindow()
    {
        (string exe, string args) cmd = LaunchCommand.Resume(null, "pwsh.exe", "C:\\Projects\\admin", Sid, "admin");

        Assert.Equal("pwsh.exe", cmd.exe);
        Assert.Equal("-NoExit -Command claude -r " + Sid, cmd.args);
        Assert.DoesNotContain("-w 0", cmd.args);   // no window/tab plumbing without wt
    }

    [Fact]
    public void NonUuidSessionIdIsRefused()
    {
        Assert.False(LaunchCommand.IsValidSessionId("; rm -rf /"));
        Assert.False(LaunchCommand.IsValidSessionId(""));
        Assert.False(LaunchCommand.IsValidSessionId(null));
        Assert.True(LaunchCommand.IsValidSessionId(Sid));
        Assert.Throws<ArgumentException>(() => LaunchCommand.Resume(null, "pwsh.exe", "C:\\p", "not-a-uuid", "t"));
    }

    [Fact]
    public void TitleIsSanitisedForWindowsTerminal()
    {
        Assert.Equal("a b", LaunchCommand.SafeTitle("a;b"));       // ';' separates wt commands
        Assert.Equal("a b", LaunchCommand.SafeTitle("a\"b"));      // '"' would end the argument
        Assert.Equal("claude", LaunchCommand.SafeTitle(""));
        Assert.Equal("claude", LaunchCommand.SafeTitle(null));
        Assert.Equal(40, LaunchCommand.SafeTitle(new string('x', 80)).Length);
    }

    [Fact]
    public void TrailingBackslashWouldEscapeTheClosingQuote()
    {
        (string exe, string args) cmd = LaunchCommand.Resume("wt.exe", "pwsh.exe", "C:\\Projects\\admin\\", Sid, "admin");
        Assert.Contains("-d \"C:\\Projects\\admin\"", cmd.args);

        (string exe, string args) root = LaunchCommand.Resume("wt.exe", "pwsh.exe", "C:\\", Sid, "admin");
        Assert.Contains("-d \"C:\\\"", root.args);   // but a drive root keeps it: "C:" means something else
    }

    [Fact]
    public void NewSessionOmitsResume()
    {
        (string exe, string args) cmd = LaunchCommand.NewSession("wt.exe", "pwsh.exe", "C:\\p\\admin", "admin");
        Assert.Contains("-Command claude", cmd.args);
        Assert.DoesNotContain("-r ", cmd.args);
    }
}
