using System.Collections.Concurrent;
using System.Diagnostics;

namespace BD.Connectors.ClaudeCode.Transport;

// Tracks live CLI subprocesses so they can be terminated when the parent .NET process exits.
// Mirrors PY's `_ACTIVE_CHILDREN` / `atexit.register(_kill_active_children)`: prevents orphaned
// `claude` processes from leaking when callers crash or exit before awaiting CloseAsync().
internal static class ProcessRegistry
{
    private static readonly ConcurrentDictionary<int, Process> _activeChildren = new();
    private static int _handlerRegistered;

    public static void Register(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (Interlocked.Exchange(ref _handlerRegistered, 1) == 0)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => KillActiveChildren();
        }

        _activeChildren[process.Id] = process;
    }

    public static void Unregister(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _activeChildren.TryRemove(process.Id, out _);
    }

    private static void KillActiveChildren()
    {
        foreach (var (_, process) in _activeChildren)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Best-effort process-exit cleanup; nothing left to log to at this point.
            }
        }

        _activeChildren.Clear();
    }
}
