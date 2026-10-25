using System.Globalization;
using System.Text;

namespace ItemLookMerger.Core.Mo2;

/// <summary>
/// A read-only INI file as MO2 writes it (Qt's QSettings format): sections, "key=value" lines, values that may be quoted,
/// carry backslash escapes or be wrapped in "@ByteArray(...)". Section and key names ignore case.
/// </summary>
internal sealed class IniDocument
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    private IniDocument() { }

    public static IniDocument Parse(string text)
    {
        var document = new IniDocument();
        var section = document.SectionNamed(string.Empty);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                section = document.SectionNamed(line[1..^1].Trim());
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            // Later duplicates win, as in QSettings.
            section[line[..equals].Trim()] = DecodeValue(line[(equals + 1)..]);
        }
        return document;
    }

    /// <summary>The decoded value, or null when the section or key is missing.</summary>
    public string? Get(string section, string key) =>
        _sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value) ? value : null;

    private Dictionary<string, string> SectionNamed(string name)
    {
        if (!_sections.TryGetValue(name, out var values))
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _sections[name] = values;
        }
        return values;
    }

    /// <summary>
    /// Turns a raw QSettings value into its text: strips "@ByteArray(...)" or surrounding quotes, then resolves the escapes
    /// (\\, \", \n, \t, \0 and \x). Qt writes the two kinds differently: inside "@ByteArray(...)" \xHH is one byte and the bytes
    /// form UTF-8 text; in a plain value \x plus up to four hex digits is one UTF-16 character.
    /// </summary>
    internal static string DecodeValue(string raw)
    {
        var value = raw.Trim();
        if (value.StartsWith("@ByteArray(", StringComparison.Ordinal) && value.EndsWith(')'))
            return DecodeByteArray(value["@ByteArray(".Length..^1]);
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1];
        return DecodePlain(value);
    }

    private static string DecodePlain(string value)
    {
        var text = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                text.Append(value[i]);
                continue;
            }
            var next = value[++i];
            switch (next)
            {
                case 'n': text.Append('\n'); break;
                case 't': text.Append('\t'); break;
                case 'r': text.Append('\r'); break;
                case '0': break;
                case 'x':
                    var hexLength = HexDigitsAt(value, i + 1, max: 4);
                    if (hexLength == 0) { text.Append('x'); break; }
                    text.Append((char)int.Parse(value.AsSpan(i + 1, hexLength), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += hexLength;
                    break;
                default: text.Append(next); break;
            }
        }
        return text.ToString();
    }

    private static int HexDigitsAt(string value, int start, int max)
    {
        var length = 0;
        while (length < max && start + length < value.Length && Uri.IsHexDigit(value[start + length])) length++;
        return length;
    }

    private static string DecodeByteArray(string value)
    {
        var bytes = new List<byte>(value.Length);
        var plainStart = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length) continue;
            // Plain text runs are encoded whole, so characters outside the BMP stay intact.
            bytes.AddRange(Encoding.UTF8.GetBytes(value[plainStart..i]));
            var next = value[++i];
            switch (next)
            {
                case 'n': bytes.Add((byte)'\n'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case '0': break;
                case 'x':
                    var hexLength = HexDigitsAt(value, i + 1, max: 2);
                    if (hexLength == 0) { bytes.Add((byte)'x'); break; }
                    bytes.Add(byte.Parse(value.AsSpan(i + 1, hexLength), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += hexLength;
                    break;
                default: bytes.AddRange(Encoding.UTF8.GetBytes(next.ToString())); break;
            }
            plainStart = i + 1;
        }
        bytes.AddRange(Encoding.UTF8.GetBytes(value[plainStart..]));
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
