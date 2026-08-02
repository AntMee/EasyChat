using System;
using System.Collections.Generic;
using System.Text;

namespace EasyChat.Shared.Streaming;

public sealed class JsonLinesEventStreamDecoder<T>
{
    private readonly Func<string, T> _deserialize;
    private readonly Action<Exception, string>? _onInvalidLine;
    private readonly StringBuilder _buffer = new();

    public JsonLinesEventStreamDecoder(
        Func<string, T> deserialize,
        Action<Exception, string>? onInvalidLine = null)
    {
        _deserialize = deserialize;
        _onInvalidLine = onInvalidLine;
    }

    public IEnumerable<T> Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            yield break;

        _buffer.Append(chunk);
        var content = _buffer.ToString();
        var start = 0;
        while (true)
        {
            var newline = content.IndexOf('\n', start);
            if (newline < 0)
                break;

            foreach (var item in ParseLine(content[start..newline]))
                yield return item;
            start = newline + 1;
        }

        if (start > 0)
        {
            _buffer.Clear();
            _buffer.Append(content[start..]);
        }
    }

    public IEnumerable<T> Complete()
    {
        var remaining = _buffer.ToString();
        _buffer.Clear();
        foreach (var item in ParseLine(remaining))
            yield return item;
    }

    private IEnumerable<T> ParseLine(string value)
    {
        var line = value.Trim();
        if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal))
            yield break;

        T item;
        try
        {
            item = _deserialize(line);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            _onInvalidLine?.Invoke(ex, line);
            yield break;
        }

        yield return item;
    }
}
