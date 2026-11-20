// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Native.Tests;

/// <summary>Type names are asked on the thread pool too (sorting by Kind runs there); they must be the shell's, not the fallback.</summary>
public sealed class ShellTypeNameTests
{
    [Fact]
    public async Task TypeNamesAskedOnTheThreadPoolAreTheShells()
    {
        var name = await Task.Run(() => ShellIcons.TypeName("notes.txt", isDirectory: false));
        Assert.NotEqual("TXT File", name);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }
}
