using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using EasyChat.Models.Translation.Selection;

namespace EasyChat.Services.Translation.Selection;

/// <summary>
/// Buffers arbitrary transport chunks and produces one value per complete JSON Lines record.
/// It deliberately has no translation-specific knowledge and can be reused by other streamed features.
/// </summary>
public sealed class JsonLinesStreamReader<T>
{
    private readonly Func<string, T> _deserialize;
    private readonly StringBuilder _buffer = new();

    public JsonLinesStreamReader(Func<string, T> deserialize)
    {
        _deserialize = deserialize;
    }

    public IEnumerable<T> Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            yield break;
        }

        _buffer.Append(chunk);
        var content = _buffer.ToString();
        var start = 0;

        while (true)
        {
            var newline = content.IndexOf('\n', start);
            if (newline < 0)
            {
                break;
            }

            var line = content[start..newline].Trim();
            start = newline + 1;

            if (!string.IsNullOrEmpty(line) && !line.StartsWith("```", StringComparison.Ordinal))
            {
                yield return _deserialize(line);
            }
        }

        _buffer.Clear();
        _buffer.Append(content[start..]);
    }

    public IEnumerable<T> Complete()
    {
        var line = _buffer.ToString().Trim();
        _buffer.Clear();

        if (!string.IsNullOrEmpty(line) && !line.StartsWith("```", StringComparison.Ordinal))
        {
            yield return _deserialize(line);
        }
    }
}

/// <summary>
/// Reads the selection-translation JSON Lines protocol while exposing the value of a
/// translation_delta.text string before its enclosing JSON object is complete. This is
/// necessary because an LLM may not emit a newline until it has finished the whole object.
/// </summary>
public sealed class SelectionTranslationStreamDecoder
{
    private readonly Func<string, SelectionTranslationStreamEvent> _deserialize;
    private readonly StringBuilder _line = new();
    private int _textCursor;
    private bool _isDeltaLine;
    private bool _textValueStarted;
    private bool _textValueCompleted;

    public SelectionTranslationStreamDecoder(Func<string, SelectionTranslationStreamEvent> deserialize)
    {
        _deserialize = deserialize;
    }

    public IEnumerable<SelectionTranslationStreamEvent> Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            yield break;
        }

        _line.Append(chunk);
        var content = _line.ToString();
        var lineStart = 0;

        while (true)
        {
            var newline = content.IndexOf('\n', lineStart);
            if (newline < 0)
            {
                break;
            }

            var completeLine = content[lineStart..newline].Trim();
            if (!string.IsNullOrEmpty(completeLine) && !completeLine.StartsWith("```", StringComparison.Ordinal))
            {
                foreach (var translationEvent in ReadPartialDeltaText(completeLine))
                {
                    yield return translationEvent;
                }

                foreach (var translationEvent in CompleteLine(completeLine))
                {
                    yield return translationEvent;
                }
            }

            lineStart = newline + 1;
            ResetLineState();
        }

        if (lineStart > 0)
        {
            _line.Clear();
            _line.Append(content[lineStart..]);
        }

        foreach (var translationEvent in ReadPartialDeltaText(_line.ToString()))
        {
            yield return translationEvent;
        }
    }

    public IEnumerable<SelectionTranslationStreamEvent> Complete()
    {
        var completeLine = _line.ToString().Trim();
        _line.Clear();
        if (!string.IsNullOrEmpty(completeLine) && !completeLine.StartsWith("```", StringComparison.Ordinal))
        {
            foreach (var translationEvent in ReadPartialDeltaText(completeLine))
            {
                yield return translationEvent;
            }

            foreach (var translationEvent in CompleteLine(completeLine))
            {
                yield return translationEvent;
            }
        }
    }

    private IEnumerable<SelectionTranslationStreamEvent> CompleteLine(string line)
    {
        // Validate the complete record even though its text was exposed incrementally.
        var translationEvent = _deserialize(line);
        if (!_isDeltaLine)
        {
            yield return translationEvent;
        }
    }

    private IEnumerable<SelectionTranslationStreamEvent> ReadPartialDeltaText(string content)
    {
        if (!_isDeltaLine)
        {
            var eventMarker = "\"event\"";
            var deltaMarker = "translation_delta";
            var eventIndex = content.IndexOf(eventMarker, StringComparison.Ordinal);
            var deltaIndex = content.IndexOf(deltaMarker, StringComparison.Ordinal);
            if (eventIndex < 0 || deltaIndex < eventIndex)
            {
                yield break;
            }

            _isDeltaLine = true;
        }

        if (!_textValueStarted)
        {
            var textKey = content.IndexOf("\"text\"", StringComparison.Ordinal);
            if (textKey < 0)
            {
                yield break;
            }

            var colon = content.IndexOf(':', textKey + 6);
            if (colon < 0)
            {
                yield break;
            }

            var quote = colon + 1;
            while (quote < content.Length && char.IsWhiteSpace(content[quote])) quote++;
            if (quote >= content.Length || content[quote] != '"')
            {
                yield break;
            }

            _textValueStarted = true;
            _textCursor = quote + 1;
        }

        var builder = new StringBuilder();
        while (_textCursor < content.Length && !_textValueCompleted)
        {
            var current = content[_textCursor];
            if (current == '"')
            {
                _textValueCompleted = true;
                _textCursor++;
                break;
            }

            if (current == '\\')
            {
                if (!TryReadEscape(content, _textCursor, out var decoded, out var consumed))
                {
                    break;
                }

                builder.Append(decoded);
                _textCursor += consumed;
                continue;
            }

            builder.Append(current);
            _textCursor++;
        }

        if (builder.Length > 0)
        {
            yield return new SelectionTranslationDeltaEvent(builder.ToString());
        }
    }

    private static bool TryReadEscape(string content, int index, out char decoded, out int consumed)
    {
        decoded = default;
        consumed = 0;
        if (index + 1 >= content.Length)
        {
            return false;
        }

        var escape = content[index + 1];
        switch (escape)
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
                var hex = content.Substring(index + 2, 4);
                if (ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var value))
                {
                    decoded = (char)value;
                    consumed = 6;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private void ResetLineState()
    {
        _textCursor = 0;
        _isDeltaLine = false;
        _textValueStarted = false;
        _textValueCompleted = false;
    }
}
