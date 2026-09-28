using System.Runtime.CompilerServices;
using System.Text;

namespace DipTick.Models.Chat;

/// <summary>
/// Reads a server-sent event stream and yields each event's data, per the WHATWG HTML standard,
/// "Server-sent events", 9.2.6 Interpreting an event stream: lines end in CRLF, LF or CR; a line
/// starting with ':' is a comment and ignored; "data:" appends its value (one leading space
/// removed) plus a LF to the buffer; a blank line dispatches the buffer without its last LF.
/// Other fields (event, id, retry) are not used by the chat APIs and are ignored.
/// </summary>
/// <remarks>
/// Bounded, because the other end is a network: no line longer than <see cref="MaxLine"/> and no
/// event larger than <see cref="MaxEvent"/>, so a hostile or broken server cannot make this
/// process hold an unbounded buffer.
/// </remarks>
internal static class EventStream
{
    public const int MaxLine = 1024 * 1024;
    public const int MaxEvent = 4 * 1024 * 1024;

    public static async IAsyncEnumerable<string> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // UTF-8, with one leading BOM stripped (detectEncodingFromByteOrderMarks does exactly that).
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: true);
        var data = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[8192];
        var lastWasCr = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancel).ConfigureAwait(false);
            if (read == 0)
            {
                // End of stream: an event not closed by a blank line is discarded (9.2.6).
                yield break;
            }

            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c == '\n' && lastWasCr)
                {
                    lastWasCr = false;
                    continue;
                }

                lastWasCr = c == '\r';
                if (c is not ('\r' or '\n'))
                {
                    if (line.Length >= MaxLine)
                    {
                        throw new InvalidDataException("The server sent a line longer than 1 MiB.");
                    }

                    line.Append(c);
                    continue;
                }

                if (line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        data.Length--;
                        yield return data.ToString();
                        data.Clear();
                    }

                    continue;
                }

                if (line[0] != ':')
                {
                    var text = line.ToString();
                    var colon = text.IndexOf(':', StringComparison.Ordinal);
                    var field = colon < 0 ? text : text[..colon];
                    var value = colon < 0 ? string.Empty : text[(colon + 1)..];
                    if (value.StartsWith(' '))
                    {
                        value = value[1..];
                    }

                    if (field == "data")
                    {
                        if (data.Length + value.Length + 1 > MaxEvent)
                        {
                            throw new InvalidDataException("The server sent an event larger than 4 MiB.");
                        }

                        data.Append(value).Append('\n');
                    }
                }

                line.Clear();
            }
        }
    }
}
