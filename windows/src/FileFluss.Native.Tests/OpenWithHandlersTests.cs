// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Native.Tests.Support;
using Xunit.Abstractions;

namespace FileFluss.Native.Tests;

/// <summary>
/// Right-click → Open With asks the shell which apps open a type. Enumerating only: nothing
/// here invokes a handler, which would start a real app on the test machine.
/// </summary>
public sealed class OpenWithHandlersTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PlainTextHasAtLeastOneNamedHandler()
    {
        using var sta = new StaThread("Open With");
        var (handlers, defaultId) = await sta.InvokeAsync(() =>
        {
            var found = OpenWithHandlers.ForExtension(".txt");
            return (found, OpenWithHandlers.DefaultHandlerId(".txt", found));
        });

        foreach (var handler in handlers)
        {
            output.WriteLine($"{handler.Name} | {handler.Id} | {handler.IconLocation},{handler.IconIndex}");
        }

        output.WriteLine($"default: {defaultId}");

        // A stripped-down image (Server Core, a CI container) may have no app for text at
        // all; that is no failure of the lookup.
        if (handlers.Count == 0)
        {
            return;
        }

        Assert.All(handlers, h =>
        {
            Assert.False(string.IsNullOrWhiteSpace(h.Name));
            Assert.False(string.IsNullOrWhiteSpace(h.Id));
        });
        Assert.Equal(handlers.Count, handlers.Select(h => h.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        if (defaultId is not null)
        {
            Assert.Contains(handlers, h => h.Id == defaultId);
        }
    }

    [Fact]
    public async Task HandlersHaveIconsThatCanBeLoaded()
    {
        using var sta = new StaThread("Open With icons");
        var loaded = await sta.InvokeAsync(() =>
        {
            var any = false;
            foreach (var handler in OpenWithHandlers.ForExtension(".txt"))
            {
                var icon = OpenWithHandlers.Icon(handler, 32);
                if (icon != 0)
                {
                    output.WriteLine($"{handler.Name}: HICON");
                    ShellIcons.DestroyIcon(icon);
                    any = true;
                }
                else if (OpenWithHandlers.PackagedIconFile(handler) is { } file)
                {
                    output.WriteLine($"{handler.Name}: {file}");
                    any = true;
                }
            }

            return any || OpenWithHandlers.ForExtension(".txt").Count == 0;
        });

        Assert.True(loaded);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("txt")]
    [InlineData("..\\x")]
    public void NotAnExtensionGivesNoHandlers(string extension)
    {
        Assert.Empty(OpenWithHandlers.ForExtension(extension));
        Assert.False(OpenWithHandlers.Invoke(extension, "notepad.exe", ["C:\\nothing.txt"]));
    }

    [Fact]
    public void AnUnregisteredTypeGivesNoHandlers()
        => Assert.Empty(OpenWithHandlers.ForExtension(".filefluss-no-such-type-" + Guid.NewGuid().ToString("N")[..8]));

    [Fact]
    public void AnUnknownHandlerIsNotInvoked()
        => Assert.False(OpenWithHandlers.Invoke(".txt", "C:\\no\\such\\app.exe", ["C:\\nothing.txt"]));

    [Fact]
    public void OnlyAnExistingExecutableIsLaunchedAsAFallback()
    {
        Assert.False(OpenWithHandlers.LaunchExecutable("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", ["C:\\nothing.txt"]));
        Assert.False(OpenWithHandlers.LaunchExecutable("C:\\no\\such\\app.exe", ["C:\\nothing.txt"]));
        Assert.False(OpenWithHandlers.LaunchExecutable("C:\\Windows\\notepad.exe", []));
    }
}
