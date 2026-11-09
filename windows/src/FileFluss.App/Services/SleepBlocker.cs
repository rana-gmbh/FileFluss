// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.InteropServices;

namespace FileFluss.App.Services;

/// <summary>
/// Keeps the PC from idling to sleep while transfers run (macOS 1.6 begins a
/// <c>ProcessInfo</c> activity with <c>idleSystemSleepDisabled</c> per transfer): a long
/// transfer left alone would otherwise stop part way when the PC idles to sleep. The display
/// may still turn off; only system sleep is held off.
///
/// <para><c>SetThreadExecutionState</c> belongs to the calling thread, so every call must come
/// from the same one — the UI thread, where <see cref="TransferCenter"/> runs.</para>
/// </summary>
public sealed class SleepBlocker(Func<uint, uint> setExecutionState)
{
    public const uint EsContinuous = 0x80000000;
    public const uint EsSystemRequired = 0x00000001;

    /// <summary>The real thing.</summary>
    public SleepBlocker()
        : this(SetThreadExecutionState)
    {
    }

    /// <summary>Whether sleep is being held off right now.</summary>
    public bool IsHolding { get; private set; }

    /// <summary>Holds off sleep while <paramref name="transfersRunning"/>, and lets go once nothing runs.</summary>
    public void Update(bool transfersRunning)
    {
        if (transfersRunning == IsHolding)
        {
            return;
        }

        // A zero answer means the call failed; try again on the next change.
        if (setExecutionState(transfersRunning ? EsContinuous | EsSystemRequired : EsContinuous) != 0)
        {
            IsHolding = transfersRunning;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
