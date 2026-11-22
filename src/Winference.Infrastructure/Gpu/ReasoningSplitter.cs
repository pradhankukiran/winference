using System.Text;

namespace Winference.Infrastructure.Gpu;

/// <summary>
/// Splits a streamed reply into a thinking model's working (inside &lt;think&gt;…&lt;/think&gt;) and its answer.
/// A tag can arrive split across pieces, so a possible partial tag is held back until it is decided.
/// </summary>
public sealed class ReasoningSplitter(bool startInReasoning = false)
{
    private const string Open = "<think>";
    private const string Close = "</think>";
    private readonly StringBuilder _pending = new();
    private bool _reasoning = startInReasoning;
    private bool _answerStarted;

    /// <summary>Returns the pieces that are now certain, each marked as working or answer.</summary>
    public IReadOnlyList<(bool IsReasoning, string Text)> Push(string text)
    {
        _pending.Append(text);
        var output = new List<(bool, string)>();
        while (true)
        {
            var buffer = _pending.ToString();
            var tag = _reasoning ? Close : Open;
            var found = buffer.IndexOf(tag, StringComparison.Ordinal);
            if (found >= 0)
            {
                Emit(output, buffer[..found]);
                _pending.Remove(0, found + tag.Length);
                _reasoning = !_reasoning;
                continue;
            }
            // Keep back only a tail that could still become the tag.
            var keep = PartialTagLength(buffer, tag);
            Emit(output, buffer[..^keep]);
            _pending.Remove(0, buffer.Length - keep);
            return output;
        }
    }

    /// <summary>Returns whatever is left when the reply ends.</summary>
    public IReadOnlyList<(bool IsReasoning, string Text)> Flush()
    {
        var output = new List<(bool, string)>();
        Emit(output, _pending.ToString());
        _pending.Clear();
        return output;
    }

    private void Emit(List<(bool, string)> output, string text)
    {
        if (!_reasoning && !_answerStarted)
        {
            // The answer starts at its first visible character, not at the newline after the working.
            text = text.TrimStart();
            if (text.Length > 0) _answerStarted = true;
        }
        if (text.Length > 0) output.Add((_reasoning, text));
    }

    private static int PartialTagLength(string buffer, string tag)
    {
        for (var length = Math.Min(tag.Length - 1, buffer.Length); length > 0; length--)
            if (buffer.EndsWith(tag[..length], StringComparison.Ordinal)) return length;
        return 0;
    }
}
