// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Native.Tests;

/// <summary>
/// "Open in Terminal" on a folder whose name came from somewhere else (a download, a synced
/// cloud folder): the name must never become part of a command.
/// </summary>
public sealed class TerminalLauncherTests
{
    private const string Hostile = @"C:\Users\me\x"" & calc & ""; new-tab cmd /c calc; %PATH%";

    [Theory]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [InlineData(@"C:\Tools\term.exe")]
    public void ShellsGetTheFolderOnlyAsTheirWorkingDirectory(string id)
    {
        var start = TerminalLauncher.StartInfo(new TerminalApp(id, id, @"C:\x\terminal.exe"), Hostile);

        Assert.Equal(Hostile, start.WorkingDirectory);
        Assert.DoesNotContain(start.ArgumentList, a => a.Contains("calc", StringComparison.Ordinal));
        Assert.Empty(start.Arguments);
    }

    [Fact]
    public void WindowsTerminalGetsTheFolderAsOneArgumentWithSemicolonsEscaped()
    {
        var start = TerminalLauncher.StartInfo(new TerminalApp("wt", "Windows Terminal", @"C:\x\wt.exe"), Hostile);

        Assert.Equal(["-d", Hostile.Replace(";", "\\;", StringComparison.Ordinal)], start.ArgumentList);

        // wt splits at every ';' not preceded by a backslash.
        var value = start.ArgumentList[1];
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == ';')
            {
                Assert.Equal('\\', value[i - 1]);
            }
        }
    }

    [Fact]
    public void GitBashGetsTheFolderAsOneArgument()
    {
        var start = TerminalLauncher.StartInfo(new TerminalApp("gitbash", "Git Bash", @"C:\x\git-bash.exe"), Hostile);

        Assert.Equal(["--cd=" + Hostile], start.ArgumentList);
        Assert.False(start.UseShellExecute);
    }
}
