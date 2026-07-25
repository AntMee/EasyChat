using System;
using System.Collections.Generic;
using System.Text;

namespace EasyChat.Services.Streaming;

public sealed class JsonLinesDeltaStreamDecoder<T>
{
    private readonly Func<string, T> _deserialize;
    private readonly string _eventName;
    private readonly string _propertyName;
    private readonly Action<Exception, string>? _onInvalidLine;
    private readonly StringBuilder _line = new();
    private int _cursor;
    private bool _isDelta;
    private bool _started;
    private bool _completed;

    public JsonLinesDeltaStreamDecoder(
        Func<string, T> deserialize,
        string eventName,
        string propertyName,
        Action<Exception, string>? onInvalidLine = null)
    {
        _deserialize = deserialize;
        _eventName = eventName;
        _propertyName = propertyName;
        _onInvalidLine = onInvalidLine;
    }

    public IEnumerable<T> Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) yield break;
        _line.Append(chunk);
        var content = _line.ToString();
        var start = 0;
        while (true)
        {
            var newline = content.IndexOf('\n', start);
            if (newline < 0) break;
            var line = content[start..newline].Trim();
            if (!string.IsNullOrEmpty(line) && !line.StartsWith("```", StringComparison.Ordinal))
            {
                foreach (var item in ReadPartial(line)) yield return item;
                foreach (var item in CompleteLine(line)) yield return item;
            }
            start = newline + 1;
            Reset();
        }

        if (start > 0)
        {
            _line.Clear();
            _line.Append(content[start..]);
        }

        foreach (var item in ReadPartial(_line.ToString())) yield return item;
    }

    public IEnumerable<T> Complete()
    {
        var line = _line.ToString().Trim();
        _line.Clear();
        if (!string.IsNullOrEmpty(line) && !line.StartsWith("```", StringComparison.Ordinal))
        {
            foreach (var item in ReadPartial(line)) yield return item;
            foreach (var item in CompleteLine(line)) yield return item;
        }
    }

    private IEnumerable<T> CompleteLine(string line)
    {
        T? item;
        try
        {
            item = _deserialize(line);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            _onInvalidLine?.Invoke(ex, line);
            yield break;
        }

        if (!_isDelta && item is not null) yield return item;
    }

    private IEnumerable<T> ReadPartial(string content)
    {
        if (!_isDelta)
        {
            var eventIndex = content.IndexOf("\"event\"", StringComparison.Ordinal);
            var markerIndex = content.IndexOf(_eventName, StringComparison.Ordinal);
            if (eventIndex < 0 || markerIndex < eventIndex) yield break;
            _isDelta = true;
        }

        if (!_started)
        {
            var key = content.IndexOf($"\"{_propertyName}\"", StringComparison.Ordinal);
            if (key < 0) yield break;
            var colon = content.IndexOf(':', key + _propertyName.Length + 2);
            if (colon < 0) yield break;
            var quote = colon + 1;
            while (quote < content.Length && char.IsWhiteSpace(content[quote])) quote++;
            if (quote >= content.Length || content[quote] != '"') yield break;
            _started = true;
            _cursor = quote + 1;
        }

        var builder = new StringBuilder();
        while (_cursor < content.Length && !_completed)
        {
            var current = content[_cursor];
            if (current == '"')
            {
                _completed = true;
                _cursor++;
                break;
            }
            if (current == '\\')
            {
                if (!TryReadEscape(content, _cursor, out var decoded, out var consumed)) yield break;
                builder.Append(decoded);
                _cursor += consumed;
                continue;
            }
            builder.Append(current);
            _cursor++;
        }

        if (builder.Length > 0)
        {
            var item = _deserialize($"{{\"event\":\"{_eventName}\",\"{_propertyName}\":{System.Text.Json.JsonSerializer.Serialize(builder.ToString())}}}");
            yield return item;
        }
    }

    private static bool TryReadEscape(string content, int index, out char decoded, out int consumed)
    {
        decoded = default;
        consumed = 0;
        if (index + 1 >= content.Length) return false;
        switch (content[index + 1])
        {
            case '"': decoded = '"'; consumed = 2; return true;
            case '\\': decoded = '\\'; consumed = 2; return true;
            case '/': decoded = '/'; consumed = 2; return true;
            case 'b': decoded = '\b'; consumed = 2; return true;
            case 'f': decoded = '\f'; consumed = 2; return true;
            case 'n': decoded = '\n'; consumed = 2; return true;
            case 'r': decoded = '\r'; consumed = 2; return true;
            case 't': decoded = '\t'; consumed = 2; return true;
            case 'u' when index + 5 < content.Length:
                if (ushort.TryParse(content.Substring(index + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out var value))
                {
                    decoded = (char)value;
                    consumed = 6;
                    return true;
                }
                return false;
            default: return false;
        }
    }

    private void Reset()
    {
        _cursor = 0;
        _isDelta = false;
        _started = false;
        _completed = false;
    }
}
