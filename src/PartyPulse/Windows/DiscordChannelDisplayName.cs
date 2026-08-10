using System.Text;

namespace PartyPulse.Windows;

internal static class DiscordChannelDisplayName
{
    public static string ToAsciiLetters(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "channel";
        }

        var normalized = value.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (character is >= 'a' and <= 'z')
            {
                builder.Append(character);
                continue;
            }

            if (character is >= 'A' and <= 'Z')
            {
                builder.Append((char)(character + ('a' - 'A')));
                continue;
            }

            AppendLatinFallback(builder, character);
        }

        return builder.Length > 0 ? builder.ToString() : "channel";
    }

    private static void AppendLatinFallback(StringBuilder builder, char character)
    {
        var replacement = character switch
        {
            '\u1D00' => "a",
            '\u0299' => "b",
            '\u1D04' => "c",
            '\u1D05' => "d",
            '\u1D07' => "e",
            '\uA730' => "f",
            '\u0262' => "g",
            '\u029C' => "h",
            '\u026A' => "i",
            '\u1D0A' => "j",
            '\u1D0B' => "k",
            '\u029F' => "l",
            '\u1D0D' => "m",
            '\u0274' => "n",
            '\u1D0F' => "o",
            '\u1D18' => "p",
            '\uA7AF' => "q",
            '\u0280' => "r",
            '\uA731' => "s",
            '\u1D1B' => "t",
            '\u1D1C' => "u",
            '\u1D20' => "v",
            '\u1D21' => "w",
            '\u028F' => "y",
            '\u1D22' => "z",
            '\u00DF' => "ss",
            '\u00C6' or '\u00E6' => "ae",
            '\u0152' or '\u0153' => "oe",
            '\u00D8' or '\u00F8' => "o",
            '\u0141' or '\u0142' => "l",
            '\u00D0' or '\u00F0' or '\u0110' or '\u0111' => "d",
            '\u00DE' or '\u00FE' => "th",
            _ => null,
        };

        if (replacement is not null)
        {
            builder.Append(replacement);
        }
    }
}
