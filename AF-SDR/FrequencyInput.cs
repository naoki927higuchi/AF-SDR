using System.Globalization;
using System.Text.RegularExpressions;

namespace AfSdr;

internal static class FrequencyInput
{
    // A dot is the decimal separator; commas are accepted only in groups of three.
    private static readonly Regex Pattern = new(
        @"\A\s*(?<number>(?:[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)(?:\.[0-9]+)?)\s*(?<prefix>[kKmMgG]?)\s*(?:[hH][zZ])?\s*\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static bool TryParse(string text, out uint hz, out string error)
    {
        hz = 0;
        error = "周波数を入力してください（例: 78.4M、8400k、78,400,000 Hz）。";
        if (text.Length > 100) return false;
        Match match = Pattern.Match(text);
        if (!match.Success || !decimal.TryParse(match.Groups["number"].Value,
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out decimal value)) return false;
        decimal multiplier = match.Groups["prefix"].Value.ToUpperInvariant() switch
        {
            "K" => 1_000m, "M" => 1_000_000m, "G" => 1_000_000_000m, _ => 1m
        };
        // Check before multiplication to avoid decimal overflow on excessive input.
        error = "周波数は1～4,294,967,295 Hzの範囲で入力してください。";
        if (value < 1m / multiplier || value > uint.MaxValue / multiplier) return false;
        decimal scaled = value * multiplier;
        error = "Hzに換算して整数になる周波数を入力してください。";
        if (scaled != decimal.Truncate(scaled)) return false;
        hz = (uint)scaled;
        error = string.Empty;
        return true;
    }

    internal static string Format(uint hz)
    {
        (decimal divisor, string suffix) = hz >= 1_000_000_000 ? (1_000_000_000m, "G")
            : hz >= 1_000_000 ? (1_000_000m, "M") : hz >= 1000 ? (1000m, "k") : (1m, "");
        return (hz / divisor).ToString("0.#########", CultureInfo.InvariantCulture) + suffix;
    }
}
