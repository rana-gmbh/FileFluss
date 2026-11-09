// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Transfers;

namespace FileFluss.Core.Tests;

/// <summary>macOS 1.6 "the conflict question always appears, queues when several arrive at once, and ends cleanly on Cancel".</summary>
public sealed class ConflictPrompterTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>A stand-in for the dialog: each question waits until the test answers it.</summary>
    private sealed class FakeDialog
    {
        private readonly Lock _gate = new();
        private readonly List<(string Name, TaskCompletionSource<ConflictResolution> Answer)> _open = [];

        public int MostOpenAtOnce { get; private set; }

        public List<string> Shown { get; } = [];

        public async Task<ConflictResolution> Show(ConflictInfo conflict, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<ConflictResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _open.Add((conflict.Source.Name, answer));
                Shown.Add(conflict.Source.Name);
                MostOpenAtOnce = Math.Max(MostOpenAtOnce, _open.Count);
            }

            // The real dialog closes itself with Stop when its transfer is cancelled.
            await using var close = ct.Register(() => answer.TrySetResult(new ConflictResolution(ConflictChoice.Stop, false)));
            try
            {
                return await answer.Task;
            }
            finally
            {
                lock (_gate)
                {
                    _open.RemoveAll(o => o.Answer == answer);
                }
            }
        }

        public async Task<string> WaitForOpenAsync()
        {
            var deadline = DateTime.UtcNow + Patience;
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_open.Count > 0)
                    {
                        return _open[0].Name;
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException("No question appeared.");
        }

        public void Answer(ConflictChoice choice)
        {
            lock (_gate)
            {
                _open[0].Answer.TrySetResult(new ConflictResolution(choice, false));
            }
        }
    }

    private static ConflictInfo Conflict(string name)
    {
        var item = new CloudFileItem("/" + name, name, "/" + name, false, 1, DateTimeOffset.UnixEpoch);
        return new ConflictInfo(item, item, 0, LocalFileProvider.Shared, LocalFileProvider.Shared);
    }

    [Fact]
    public async Task QuestionsArriveOneAtATimeInTheOrderAsked()
    {
        var dialog = new FakeDialog();
        var prompter = new ConflictPrompter(dialog.Show);

        var first = prompter.AskAsync(Conflict("first"), CancellationToken.None);
        Assert.Equal("first", await dialog.WaitForOpenAsync());
        var second = prompter.AskAsync(Conflict("second"), CancellationToken.None);
        var third = prompter.AskAsync(Conflict("third"), CancellationToken.None);
        Assert.Equal(2, prompter.WaitingCount);

        dialog.Answer(ConflictChoice.Replace);
        Assert.Equal(ConflictChoice.Replace, (await first.WaitAsync(Patience)).Choice);
        Assert.Equal("second", await dialog.WaitForOpenAsync());
        dialog.Answer(ConflictChoice.Skip);
        Assert.Equal(ConflictChoice.Skip, (await second.WaitAsync(Patience)).Choice);
        Assert.Equal("third", await dialog.WaitForOpenAsync());
        dialog.Answer(ConflictChoice.KeepBoth);
        Assert.Equal(ConflictChoice.KeepBoth, (await third.WaitAsync(Patience)).Choice);

        Assert.Equal(1, dialog.MostOpenAtOnce);
        Assert.Equal(["first", "second", "third"], dialog.Shown);
    }

    [Fact]
    public async Task CancellingTheTransferOnScreenStopsItAndShowsTheNext()
    {
        var dialog = new FakeDialog();
        var prompter = new ConflictPrompter(dialog.Show);
        using var cancel = new CancellationTokenSource();

        var onScreen = prompter.AskAsync(Conflict("cancelled"), cancel.Token);
        await dialog.WaitForOpenAsync();
        var next = prompter.AskAsync(Conflict("next"), CancellationToken.None);

        await cancel.CancelAsync();

        Assert.Equal(ConflictChoice.Stop, (await onScreen.WaitAsync(Patience)).Choice);
        Assert.Equal("next", await dialog.WaitForOpenAsync());
        dialog.Answer(ConflictChoice.Replace);
        Assert.Equal(ConflictChoice.Replace, (await next.WaitAsync(Patience)).Choice);
    }

    [Fact]
    public async Task ACancelledTransferWaitingInLineIsNeverAsked()
    {
        var dialog = new FakeDialog();
        var prompter = new ConflictPrompter(dialog.Show);
        using var cancel = new CancellationTokenSource();

        var onScreen = prompter.AskAsync(Conflict("on screen"), CancellationToken.None);
        await dialog.WaitForOpenAsync();
        var waiting = prompter.AskAsync(Conflict("withdrawn"), cancel.Token);

        await cancel.CancelAsync();
        Assert.Equal(ConflictChoice.Stop, (await waiting.WaitAsync(Patience)).Choice);
        Assert.Equal(0, prompter.WaitingCount);

        dialog.Answer(ConflictChoice.Skip);
        Assert.Equal(ConflictChoice.Skip, (await onScreen.WaitAsync(Patience)).Choice);
        Assert.Equal(["on screen"], dialog.Shown);

        // And the line is free again afterwards.
        var later = prompter.AskAsync(Conflict("later"), CancellationToken.None);
        Assert.Equal("later", await dialog.WaitForOpenAsync());
        dialog.Answer(ConflictChoice.Replace);
        Assert.Equal(ConflictChoice.Replace, (await later.WaitAsync(Patience)).Choice);
    }

    [Fact]
    public async Task AnAlreadyCancelledTransferGetsStopWithoutAQuestion()
    {
        var dialog = new FakeDialog();
        var prompter = new ConflictPrompter(dialog.Show);

        var answer = await prompter.AskAsync(Conflict("x"), new CancellationToken(canceled: true));

        Assert.Equal(ConflictChoice.Stop, answer.Choice);
        Assert.Empty(dialog.Shown);
    }

    [Fact]
    public async Task AQuestionThatFailsDoesNotBlockTheNext()
    {
        var calls = 0;
        var prompter = new ConflictPrompter((_, _) => ++calls == 1
            ? throw new InvalidOperationException("dialog broke")
            : Task.FromResult(new ConflictResolution(ConflictChoice.Skip, false)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => prompter.AskAsync(Conflict("a"), CancellationToken.None));
        Assert.Equal(ConflictChoice.Skip, (await prompter.AskAsync(Conflict("b"), CancellationToken.None).WaitAsync(Patience)).Choice);
    }
}
