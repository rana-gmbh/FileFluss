// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Search;

/// <summary>
/// The thread observable state belongs to: the <see cref="SynchronizationContext"/> current
/// when the owner was created (the UI thread in the app), or none (tests, console), in which
/// case work runs inline. Background work posts its state changes here so bound views are
/// only ever notified on their own thread.
/// </summary>
internal sealed class UiContext
{
    private readonly SynchronizationContext? _context;

    private UiContext(SynchronizationContext? context) => _context = context;

    public static UiContext Capture() => new(SynchronizationContext.Current);

    public void Post(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            action();
        }
        else
        {
            _context.Post(static state => ((Action)state!)(), action);
        }
    }
}
