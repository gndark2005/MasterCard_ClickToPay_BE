using System.Text;

namespace PowerTranz3DSecurePoc.Demo;

public sealed record ConsoleLine(string Text, bool IsError);

/// <summary>
/// Tees Console.Out/Console.Error into an in-memory list so the browser can mirror
/// exactly what the console printed (already masked, so no extra secret handling is needed).
/// </summary>
public sealed class ConsoleRecorder
{
    private readonly List<ConsoleLine> _lines = [];
    private readonly Lock _gate = new();

    public void Install()
    {
        Console.SetOut(new TeeWriter(Console.Out, this, isError: false));
        Console.SetError(new TeeWriter(Console.Error, this, isError: true));
    }

    public IReadOnlyList<ConsoleLine> Snapshot()
    {
        lock (_gate) return [.. _lines];
    }

    private void Add(string text, bool isError)
    {
        lock (_gate) _lines.Add(new ConsoleLine(text, isError));
    }

    private sealed class TeeWriter(TextWriter inner, ConsoleRecorder recorder, bool isError) : TextWriter
    {
        private readonly StringBuilder _current = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value) => Write(value.ToString());

        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            lock (_current)
            {
                inner.Write(value);
                foreach (var c in value)
                {
                    if (c == '\n')
                    {
                        recorder.Add(_current.ToString().TrimEnd('\r'), isError);
                        _current.Clear();
                    }
                    else
                    {
                        _current.Append(c);
                    }
                }
            }
        }

        public override void Flush() => inner.Flush();
    }
}
