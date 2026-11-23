// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Xml.Linq;
using FileFluss.App.Tests.Support;

namespace FileFluss.App.Tests;

/// <summary>
/// Toolbar toggles act however they are toggled. A screen reader toggles through UI
/// Automation, which flips IsChecked without a click: the Hidden Files button wired to Click
/// then showed "on" while nothing had changed.
/// </summary>
public sealed class ToolbarToggleTests
{
    [Fact]
    public void AutomationToggleRaisesCheckedButNotClick()
    {
        Bench.OnSta(() =>
        {
            var button = new ToggleButton();
            var clicks = 0;
            var changes = new List<bool?>();
            button.Click += (_, _) => clicks++;
            button.Checked += (_, _) => changes.Add(button.IsChecked);
            button.Unchecked += (_, _) => changes.Add(button.IsChecked);

            var toggle = (IToggleProvider)new ToggleButtonAutomationPeer(button).GetPattern(PatternInterface.Toggle)!;
            toggle.Toggle();
            toggle.Toggle();

            // Why the toolbar listens to Checked/Unchecked, not Click.
            Assert.Equal(0, clicks);
            Assert.Equal([true, false], changes);
        });
    }

    [Fact]
    public void NoToolbarToggleActsOnClickAlone()
    {
        var xaml = XDocument.Load(MainWindowXaml());
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var toggles = xaml.Descendants(presentation + "ToggleButton").ToList();
        Assert.NotEmpty(toggles);
        foreach (var toggle in toggles)
        {
            var name = (string?)toggle.Attribute("Content") ?? "?";
            var bound = ((string?)toggle.Attribute("IsChecked"))?.StartsWith("{Binding", StringComparison.Ordinal) == true;
            var handled = toggle.Attribute("Checked") is not null && toggle.Attribute("Unchecked") is not null;
            Assert.True(bound || handled, $"{name} must act on IsChecked (a binding or Checked/Unchecked), not on Click");
            Assert.Null(toggle.Attribute("Click"));
        }
    }

    private static string MainWindowXaml([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "FileFluss.App", "MainWindow.xaml"));
}
