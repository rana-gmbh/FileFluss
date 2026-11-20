// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Commands;

namespace FileFluss.Core.Tests;

public sealed class KeyboardCommandTests
{
    [Theory]
    [InlineData(KeyboardCommand.ParentDirectory)]
    [InlineData(KeyboardCommand.HistoryBack)]
    [InlineData(KeyboardCommand.HistoryForward)]
    [InlineData(KeyboardCommand.ToggleActivePanel)]
    public void HoldingANavigationKeyKeepsMoving(KeyboardCommand command) => Assert.True(command.RepeatsWhenHeld());

    [Theory]
    [InlineData(KeyboardCommand.CopyToOtherPanel)]
    [InlineData(KeyboardCommand.MoveToOtherPanel)]
    [InlineData(KeyboardCommand.DeleteToTrash)]
    [InlineData(KeyboardCommand.Duplicate)]
    [InlineData(KeyboardCommand.PasteFiles)]
    [InlineData(KeyboardCommand.NewFolder)]
    [InlineData(KeyboardCommand.OpenDirectory)]
    [InlineData(KeyboardCommand.QuickLook)]
    [InlineData(KeyboardCommand.SyncPanels)]
    [InlineData(KeyboardCommand.OpenInTerminal)]
    public void HoldingAnActionKeyRunsItOnce(KeyboardCommand command) => Assert.False(command.RepeatsWhenHeld());

    [Fact]
    public void OnlyNavigationRepeats()
    {
        // A command added later must opt in; by default a held key runs it once.
        var repeating = Enum.GetValues<KeyboardCommand>().Where(c => c.RepeatsWhenHeld()).ToList();
        Assert.All(repeating, c => Assert.Equal(KeyboardSection.PanelNavigation, c.Section()));
    }
}
