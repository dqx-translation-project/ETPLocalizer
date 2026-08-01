using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ETPLocalizer;

// Shared JSON output settings for every "all"/"all-wii"/"tojson"/"port-translations" writer.
//
//   - LF-only newlines: both the indentation newlines inside the JSON and a single
//     trailing LF at end of file (JsonSerializer never appends one on its own).
//   - Minimal escaping: JavaScriptEncoder.UnsafeRelaxedJsonEscaping still escapes many
//     "safe" non-ASCII characters as \uXXXX -- notably U+3000 IDEOGRAPHIC SPACE ("　"),
//     which shows up constantly in DQX Japanese text. MinimalJsonEncoder only escapes
//     what RFC 8259 actually requires (quote, backslash, control characters), so those
//     characters round-trip as literal UTF-8 instead of escape sequences.
internal static class JsonIo
{
    private sealed class MinimalJsonEncoder : JavaScriptEncoder
    {
        public static readonly MinimalJsonEncoder Instance = new();

        public override int MaxOutputCharactersPerInputCharacter => 6; // worst case: \u00XX

        private static bool NeedsEscaping(int scalar) =>
            scalar == '"' || scalar == '\\' || scalar < 0x20;

        public override bool WillEncode(int unicodeScalar) => NeedsEscaping(unicodeScalar);

        public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
        {
            for (int i = 0; i < textLength; i++)
                if (NeedsEscaping(text[i])) return i;
            return -1;
        }

        public override unsafe bool TryEncodeUnicodeScalar(
            int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
        {
            if (!NeedsEscaping(unicodeScalar))
            {
                if (!Rune.TryCreate(unicodeScalar, out Rune rune))
                {
                    numberOfCharactersWritten = 0;
                    return false;
                }
                return rune.TryEncodeToUtf16(new Span<char>(buffer, bufferLength), out numberOfCharactersWritten);
            }

            if (unicodeScalar is '"' or '\\')
            {
                if (bufferLength < 2) { numberOfCharactersWritten = 0; return false; }
                buffer[0] = '\\';
                buffer[1] = (char)unicodeScalar;
                numberOfCharactersWritten = 2;
                return true;
            }

            // Standard single-letter escapes (RFC 8259) for the control characters that have
            // one -- most importantly the newline character, so it stays as a short
            // two-character escape instead of the six-character hex form below.
            char shortEscape = unicodeScalar switch
            {
                0x08 => 'b',
                0x09 => 't',
                0x0A => 'n',
                0x0C => 'f',
                0x0D => 'r',
                _ => '\0',
            };
            if (shortEscape != '\0')
            {
                if (bufferLength < 2) { numberOfCharactersWritten = 0; return false; }
                buffer[0] = '\\';
                buffer[1] = shortEscape;
                numberOfCharactersWritten = 2;
                return true;
            }

            // Remaining control characters (< 0x20, no short form) → \u00XX
            if (bufferLength < 6) { numberOfCharactersWritten = 0; return false; }
            const string hex = "0123456789abcdef";
            buffer[0] = '\\';
            buffer[1] = 'u';
            buffer[2] = '0';
            buffer[3] = '0';
            buffer[4] = hex[(unicodeScalar >> 4) & 0xF];
            buffer[5] = hex[unicodeScalar & 0xF];
            numberOfCharactersWritten = 6;
            return true;
        }
    }

    public static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = MinimalJsonEncoder.Instance,
        NewLine = "\n",
    };

    // Serializes `value` to `path` with LF-only newlines (including a trailing EOF LF)
    // and without over-escaping non-ASCII characters. Overwrites any existing file.
    public static void WriteFile<T>(string path, T value)
    {
        using var f = File.Create(path);
        JsonSerializer.Serialize(f, value, WriteOpts);
        f.WriteByte((byte)'\n');
    }
}
