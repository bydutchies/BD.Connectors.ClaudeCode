namespace BD.Connectors.ClaudeCode.Transport;

// Reassembles complete lines from a stream that yields arbitrary byte chunks: a chunk boundary can
// fall anywhere, including inside a multi-byte UTF-8 sequence, so splitting happens on the raw bytes
// (0x0A) before anything is decoded as text -- "\n" never appears inside a well-formed UTF-8 sequence,
// so this is always safe.
internal sealed class LineFramer
{
    private readonly List<byte> _pending = [];

    public int PendingLength => _pending.Count;

    // Adds a chunk (only the first `count` bytes of it), returning any lines it completed.
    public IReadOnlyList<byte[]> Push(byte[] chunk, int count)
    {
        List<byte[]>? lines = null;
        var start = 0;
        for (var i = 0; i < count; i++)
        {
            if (chunk[i] != (byte)'\n')
            {
                continue;
            }

            _pending.AddRange(new ArraySegment<byte>(chunk, start, i - start));
            (lines ??= []).Add([.. _pending]);
            _pending.Clear();
            start = i + 1;
        }

        if (start < count)
        {
            _pending.AddRange(new ArraySegment<byte>(chunk, start, count - start));
        }

        return lines ?? [];
    }

    // Takes the trailing partial line, if any.
    public byte[] Flush()
    {
        var result = _pending.ToArray();
        _pending.Clear();
        return result;
    }
}
