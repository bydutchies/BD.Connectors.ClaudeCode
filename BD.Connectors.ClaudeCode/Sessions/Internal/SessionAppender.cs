using System.Text;

namespace BD.Connectors.ClaudeCode.Sessions.Internal;

// Appends data to an existing session file without creating it if missing. PY opens with
// O_WRONLY | O_APPEND (no O_CREAT) for an atomic, TOCTOU-free append (PY's `_try_append` docstring).
// This implementation instead opens for Write and seeks to end before each
// write — see PORTING_STATUS.md "Afwijkingen van PY" (Fase 6) for why a true O_APPEND P/Invoke was
// deferred (FileMode.Append would create the file when missing, which PY deliberately avoids; the
// raw O_APPEND flag value itself differs between Linux and macOS).
internal static class SessionAppender
{
    // Returns true on successful write, false if the file does not exist or is a 0-byte stub — both
    // are a "session not here, keep searching" signal the caller already handles.
    public static bool TryAppend(string path, string data)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        using (stream)
        {
            if (stream.Length == 0)
            {
                return false;
            }

            stream.Seek(0, SeekOrigin.End);
            var bytes = Encoding.UTF8.GetBytes(data);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
            return true;
        }
    }
}
