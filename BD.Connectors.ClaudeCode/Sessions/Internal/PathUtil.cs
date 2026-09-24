using System.Text;

namespace BD.Connectors.ClaudeCode.Sessions.Internal;

// Best-effort realpath: resolves symlinks component-by-component, falling back to a plain
// Path.GetFullPath when a component can't be inspected (permissions, transient I/O, a path that
// doesn't exist). See PORTING_STATUS.md "Afwijkingen van PY" (Fase 6) for why this is a managed
// per-component walk rather than a native realpath(3)/CreateFileW P/Invoke.
internal static class PathUtil
{
    private const int MaxSymlinkHops = 40;

    // Mirrors PY's os.path.realpath(d) + unicodedata.normalize("NFC", ...) (PY's `_canonicalize_path`).
    // Exceptions from Path.GetFullPath (bad path syntax etc.) propagate, matching
    // realpath's own OSError — callers (SessionPaths.CanonicalizePath) catch and fall back to NFC(input).
    public static string RealPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string resolved;
        try
        {
            resolved = ResolvePerComponent(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            resolved = Path.GetFullPath(path);
        }

        return resolved.Normalize(NormalizationForm.FormC);
    }

    private static string ResolvePerComponent(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var rest = fullPath[root.Length..];
        var segments = rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var current = root.TrimEnd(Path.DirectorySeparatorChar);
        if (current.Length == 0)
        {
            current = Path.DirectorySeparatorChar.ToString();
        }

        var hops = 0;
        foreach (var segment in segments)
        {
            var candidate = Path.Combine(current, segment);
            current = FollowSymlinkChain(candidate, ref hops);
        }

        return current;
    }

    private static string FollowSymlinkChain(string candidate, ref int hops)
    {
        var current = candidate;
        while (true)
        {
            FileSystemInfo? info =
                Directory.Exists(current) ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current)
                : null;

            if (info is null || !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return current;
            }

            if (++hops > MaxSymlinkHops)
            {
                // Mirrors realpath(3)'s ELOOP behavior: give up rather than spin forever on a cycle.
                return current;
            }

            var target = info.LinkTarget;
            if (target is null)
            {
                return current;
            }

            current = Path.IsPathRooted(target)
                ? Path.GetFullPath(target)
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current) ?? string.Empty, target));
        }
    }
}
