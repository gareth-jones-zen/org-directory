using System.Globalization;
using System.Text;
using EdgeDevTools;

namespace OrgDirectory;

/// <summary>A ValueError, as the Python version raised for settings that can't be used (exit code 2).</summary>
public class PyValueError(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The bits of Python's behaviour this tool's output depends on, where .NET's differs: lower-casing,
/// sorting by code point (and stably), html.escape, the csv module's quoting, str.isdigit and the date formats.</summary>
public static class Py
{
    /// <summary>str.lower(): .NET's differs for U+0130 and a final capital sigma (see EdgeProfile.PythonLower).</summary>
    public static string Lower(string text) => EdgeProfile.PythonLower(text);

    /// <summary>Python compares strings by code point; .NET's ordinal comparison is by UTF-16 unit, which puts a
    /// character above U+FFFF (two units starting U+D8xx) before U+E000 to U+FFFF.</summary>
    public static readonly IComparer<string> CodePoints = Comparer<string>.Create(CompareCodePoints);

    public static int CompareCodePoints(string? a, string? b)
    {
        if (ReferenceEquals(a, b))
            return 0;
        if (a is null)
            return -1;
        if (b is null)
            return 1;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            if (a[i] == b[i])
                continue;
            return Order(a[i]).CompareTo(Order(b[i]));
        }
        return a.Length.CompareTo(b.Length);
    }

    // Surrogates (U+D800 to U+DFFF) stand for code points above U+FFFF, so they sort after U+E000 to U+FFFF.
    private static int Order(char c) => c is >= '\ud800' and <= '\udfff' ? c + 0x2000 : c >= '' ? c - 0x800 : c;

    /// <summary>sorted(items, key=key): stable, by code point.</summary>
    public static List<T> Sorted<T>(IEnumerable<T> items, Func<T, string> key) => items.OrderBy(key, CodePoints).ToList();

    /// <summary>html.escape(text, quote=True).</summary>
    public static string HtmlEscape(string text)
    {
        var b = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            b.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#x27;",
                _ => c.ToString(),
            });
        }
        return b.ToString();
    }

    /// <summary>str.isdigit() for the code point at index: any character with a Unicode digit value, as Python
    /// tests it (decimal digits, and others such as superscripts).</summary>
    public static bool IsDigit(string text, int index) => CharUnicodeInfo.GetDigitValue(text, index) >= 0;

    /// <summary>A row as csv.writer(lineterminator="\n") writes it: QUOTE_MINIMAL, so a field is quoted only if it
    /// holds a comma, a quote, a line break; quotes inside are doubled. A row of one empty field is written as "".</summary>
    public static string CsvRow(IReadOnlyList<string> fields)
    {
        var b = new StringBuilder();
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
                b.Append(',');
            var field = fields[i];
            var quote = field.AsSpan().IndexOfAny(",\"\r\n") >= 0 || (fields.Count == 1 && field.Length == 0);
            if (quote)
                b.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
            else
                b.Append(field);
        }
        return b.Append('\n').ToString();
    }

    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>f"{when:%d %b %Y %H:%M}", as Python formats it on Windows (English month names).</summary>
    public static string Stamp(DateTime when) =>
        string.Create(CultureInfo.InvariantCulture, $"{when.Day:00} {Months[when.Month - 1]} {when.Year:0000} {when.Hour:00}:{when.Minute:00}");

    /// <summary>when.isoformat(timespec="seconds") for a naive datetime.</summary>
    public static string IsoSeconds(DateTime when) => when.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>date.isoformat().</summary>
    public static string IsoDate(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>datetime.fromisoformat(text) for the naive forms this tool writes ("2026-11-08T09:00:00", with or
    /// without seconds or a fraction, "T" or a space). Throws PyValueError for anything else, including a time with
    /// an offset (which Python would read, then fail to compare with a naive now).</summary>
    public static DateTime FromIsoFormat(string text)
    {
        string[] formats = ["yyyy-MM-dd", "yyyy-MM-dd'T'HH", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
                            "yyyy-MM-dd HH", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF"];
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
            return when;
        throw new PyValueError($"Invalid isoformat string: {PyCompat.Py.ReprString(text)}");
    }

    /// <summary>(a - b).days: whole days, rounded down (so a negative difference of half a day is -1).</summary>
    public static long Days(DateTime a, DateTime b) => (long)Math.Floor((a - b).TotalDays);

    /// <summary>repr(str): in single quotes (double if it holds a ' and no "), with backslash, tab, newline and carriage
    /// return escaped, and every character Python doesn't count as printable escaped too (controls, format and
    /// private-use characters, surrogates, unassigned code points, and separators other than the space).</summary>
    public static string Repr(string text)
    {
        var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
        var b = new StringBuilder().Append(quote);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            int code = c;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                code = char.ConvertToUtf32(c, text[++i]);
            if (code == '\\' || code == quote)
                b.Append('\\').Append((char)code);
            else if (code == '\t')
                b.Append("\\t");
            else if (code == '\n')
                b.Append("\\n");
            else if (code == '\r')
                b.Append("\\r");
            else if (code < 0x20 || code == 0x7f || !Printable(code))
                b.Append(code <= 0xff ? $"\\x{code:x2}" : code <= 0xffff ? $"\\u{code:x4}" : $"\\U{code:x8}");
            else if (code > 0xffff)
                b.Append(char.ConvertFromUtf32(code));
            else
                b.Append((char)code);
        }
        return b.Append(quote).ToString();
    }

    private static bool Printable(int code)
    {
        if (code == ' ')
            return true;
        if (code is >= 0xd800 and <= 0xdfff)
            return false;
        return CharUnicodeInfo.GetUnicodeCategory(code) is not (UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator);
    }

    /// <summary>Python's str[:n]: the first n code points.</summary>
    public static string Head(string text, int count) => PyCompat.PyStr.Head(text, count);
}
