// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Commands;

namespace FileFluss.Core.Tests;

public sealed class KeyboardCommandTests
{
    /// <summary>
    /// List and gallery have shortcuts of their own in both layouts (macOS ⌃⌘1 / ⌃⌘2), on keys
    /// no other command uses — a clash would silently take one of them away.
    /// </summary>
    [Theory]
    [InlineData(KeyboardPreset.Explorer)]
    [InlineData(KeyboardPreset.Commander)]
    public void TheViewModesHaveShortcutsNothingElseUses(KeyboardPreset preset)
    {
        var defaults = KeyboardCommandInfo.Defaults(preset);
        foreach (var command in new[] { KeyboardCommand.ViewAsList, KeyboardCommand.ViewAsGallery })
        {
            Assert.True(defaults.TryGetValue(command, out var binding));
            Assert.DoesNotContain(defaults, pair => pair.Key != command && pair.Value == binding);
            Assert.Equal(KeyboardSection.ViewSort, command.Section());
        }
    }

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
