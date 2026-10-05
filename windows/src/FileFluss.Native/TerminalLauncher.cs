// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using FileFluss.Core.Providers;
using static FileFluss.Core.Localization;

namespace FileFluss.Native;

/// <summary>A terminal "Open in Terminal" can start (macOS issue #61).</summary>
public sealed record TerminalApp(string Id, string Name, string Executable);

/// <summary>
/// Opens a terminal at a folder. Offers the terminals actually installed — Windows Terminal,
/// PowerShell 7, Windows PowerShell, Command Prompt, Git Bash — plus any .exe the user picked.
/// </summary>
public static class TerminalLauncher
{
    public static IReadOnlyList<TerminalApp> InstalledTerminals()
    {
        var list = new List<TerminalApp>();
        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        var system = Environment.SystemDirectory;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        AddIfExists(list, "wt", "Windows Terminal", Path.Combine(windowsApps, "wt.exe"));
        AddIfExists(list, "pwsh", "PowerShell 7", FindOnPath("pwsh.exe") ?? Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
        AddIfExists(list, "powershell", "Windows PowerShell", Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"));
        AddIfExists(list, "cmd", "Command Prompt", Path.Combine(system, "cmd.exe"));
        AddIfExists(list, "gitbash", "Git Bash", Path.Combine(programFiles, "Git", "git-bash.exe"));
        return list;
    }

    /// <summary>The configured terminal, falling back to the best installed one.</summary>
    public static TerminalApp? Resolve(string? configured)
    {
        var installed = InstalledTerminals();
        if (!string.IsNullOrEmpty(configured))
        {
            var known = installed.FirstOrDefault(t => t.Id == configured);
            if (known is not null)
            {
                return known;
            }

            if (configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(configured))
            {
                return new TerminalApp(configured, Path.GetFileNameWithoutExtension(configured), configured);
            }
        }

        return installed.FirstOrDefault();
    }

    public static void Open(string directory, string? configured)
    {
        if (!Directory.Exists(directory))
        {
            throw CloudProviderException.CommandFailed(L("{0} is not a folder.", directory));
        }

        var terminal = Resolve(configured) ?? throw CloudProviderException.CommandFailed(L("No terminal application could be found."));
        var start = new ProcessStartInfo(terminal.Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = directory,
        };
        switch (terminal.Id)
        {
            case "wt":
                start.ArgumentList.Add("-d");
                start.ArgumentList.Add(directory);
                start.UseShellExecute = true;
                break;
            case "pwsh":
            case "powershell":
                start.ArgumentList.Add("-NoExit");
                start.UseShellExecute = true;
                break;
            case "cmd":
                start.ArgumentList.Add("/K");
                start.UseShellExecute = true;
                break;
            case "gitbash":
                start.ArgumentList.Add("--cd=" + directory);
                break;
            default:
                start.UseShellExecute = true;
                break;
        }

        Process.Start(start);
    }

    private static void AddIfExists(List<TerminalApp> list, string id, string name, string path)
    {
        if (File.Exists(path))
        {
            list.Add(new TerminalApp(id, name, path));
        }
    }

    private static string? FindOnPath(string executable)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim(), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}
