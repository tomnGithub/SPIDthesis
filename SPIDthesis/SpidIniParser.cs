using System.Globalization;
using System.Text.RegularExpressions;

namespace SPIDThesis;

internal static class SpidIniParser
{
    private static readonly Regex Bars = new(@"\s*\|\s*", RegexOptions.Compiled);
    private static readonly Regex Commas = new(@"\s*,\s*", RegexOptions.Compiled);

    public static IReadOnlyList<SpidRule> ParseFiles(IEnumerable<string> paths, Action<string> warn)
    {
        var rules = new List<SpidRule>();
        foreach (var path in paths)
        {
            ParseFile(path, rules, warn);
        }

        return rules;
    }

    private static void ParseFile(string path, ICollection<SpidRule> output, Action<string> warn)
    {
        bool inNamedSection = false;
        int lineNumber = 0;

        foreach (var originalLine in File.ReadLines(path))
        {
            lineNumber++;
            var line = originalLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inNamedSection = line.Length > 2;
                continue;
            }

            // SPID reads only the unnamed/default INI section.
            if (inNamedSection)
            {
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var rawKey = line[..equals].Trim();
            var rawValue = line[(equals + 1)..].Trim();
            if (!TryParseKind(rawKey, out var kind, out var hadFinal))
            {
                continue;
            }

            try
            {
                var sanitized = Sanitize(rawValue);
                var pieces = sanitized.Split('|');
                if (pieces.Length > 7)
                {
                    warn($"{Path.GetFileName(path)}:{lineNumber}: expected at most 7 SPID fields, found {pieces.Length}. Rule skipped.");
                    continue;
                }

                Array.Resize(ref pieces, 7);
                for (int i = 0; i < pieces.Length; i++)
                {
                    pieces[i] = NormalizeOptional(pieces[i]);
                }

                if (string.IsNullOrWhiteSpace(pieces[0]))
                {
                    warn($"{Path.GetFileName(path)}:{lineNumber}: missing distributed form. Rule skipped.");
                    continue;
                }

                var rule = new SpidRule
                {
                    Kind = kind,
                    RawDistributedForm = pieces[0],
                    StringFilters = ParseTextFilters(pieces[1]),
                    FormFilters = ParseFormFilters(pieces[2]),
                    LevelFilters = ParseLevelFilters(pieces[3], path, lineNumber, warn),
                    Traits = ParseTraits(pieces[4]),
                    Chance = ParseChance(pieces[6], path, lineNumber, warn),
                    SourcePath = path,
                    LineNumber = lineNumber,
                    RawLine = originalLine,
                    HadUnsupportedFinalPrefix = hadFinal
                };

                output.Add(rule);
            }
            catch (Exception ex)
            {
                warn($"{Path.GetFileName(path)}:{lineNumber}: failed to parse rule: {ex.Message}");
            }
        }
    }

    private static bool TryParseKind(string rawKey, out DistributionKind kind, out bool hadFinal)
    {
        hadFinal = rawKey.StartsWith("Final", StringComparison.OrdinalIgnoreCase);
        var key = hadFinal ? rawKey[5..] : rawKey;

        if (key.Equals("Keyword", StringComparison.OrdinalIgnoreCase))
        {
            kind = DistributionKind.Keyword;
            return true;
        }

        if (key.Equals("Spell", StringComparison.OrdinalIgnoreCase))
        {
            kind = DistributionKind.Spell;
            return true;
        }

        if (key.Equals("Perk", StringComparison.OrdinalIgnoreCase))
        {
            kind = DistributionKind.Perk;
            return true;
        }

        kind = default;
        return false;
    }

    private static string Sanitize(string value)
    {
        var result = value;
        if (!result.Contains('~'))
        {
            result = result.Replace(" - ", "~", StringComparison.Ordinal);
        }

        result = Bars.Replace(result, "|");
        result = Commas.Replace(result, ",");
        return result.Trim();
    }

    private static string NormalizeOptional(string? value)
    {
        var result = (value ?? string.Empty).Trim();
        return IsNoneToken(result) ? string.Empty : result;
    }

    internal static bool IsNoneToken(string value)
    {
        return value.Equals("NONE", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("NULL", StringComparison.OrdinalIgnoreCase);
    }

    private static TextFilterSet ParseTextFilters(string value)
    {
        var filters = new TextFilterSet();
        foreach (var raw in SplitNonEmpty(value, ','))
        {
            var token = raw.Trim();
            if (token.Contains('+'))
            {
                filters.All.AddRange(SplitNonEmpty(token, '+'));
            }
            else if (token.StartsWith('-'))
            {
                var trimmed = token[1..].Trim();
                if (trimmed.Length > 0) filters.Not.Add(trimmed);
            }
            else if (token.StartsWith('*'))
            {
                var trimmed = token[1..].Trim();
                if (trimmed.Length > 0) filters.Partial.Add(trimmed);
            }
            else
            {
                filters.Match.Add(token);
            }
        }

        return filters;
    }

    private static RawFormFilterSet ParseFormFilters(string value)
    {
        var filters = new RawFormFilterSet();
        foreach (var raw in SplitNonEmpty(value, ','))
        {
            var token = raw.Trim();
            if (token.Contains('+'))
            {
                filters.All.AddRange(SplitNonEmpty(token, '+'));
            }
            else if (token.StartsWith('-'))
            {
                var trimmed = token[1..].Trim();
                if (trimmed.Length > 0) filters.Not.Add(trimmed);
            }
            else
            {
                filters.Match.Add(token);
            }
        }

        return filters;
    }

    private static LevelFilter ParseLevelFilters(string value, string path, int lineNumber, Action<string> warn)
    {
        var result = new LevelFilter();
        foreach (var token in SplitNonEmpty(value, ','))
        {
            if (token.Contains('('))
            {
                if (TryParseSkillRange(token, out var skill))
                {
                    result.Skills.Add(skill);
                }
                else
                {
                    warn($"{Path.GetFileName(path)}:{lineNumber}: could not parse skill filter '{token}'. The rule will fail closed for skill matching.");
                    result.Skills.Add(new SkillRange(-1, new IntRange(0, 0), false));
                }

                continue;
            }

            if (TryParseRange(token, '/', out var actorRange))
            {
                result.ActorLevel = actorRange;
            }
            else
            {
                warn($"{Path.GetFileName(path)}:{lineNumber}: could not parse actor level filter '{token}'.");
            }
        }

        return result;
    }

    private static bool TryParseSkillRange(string token, out SkillRange result)
    {
        result = default!;
        var isWeight = token.TrimStart().StartsWith('w');
        var numbers = Regex.Matches(token, @"-?\d+")
            .Cast<Match>()
            .Select(x => int.Parse(x.Value, CultureInfo.InvariantCulture))
            .ToArray();

        if (numbers.Length < 2 || numbers[0] < 0 || numbers[0] >= 18)
        {
            return false;
        }

        var range = numbers.Length >= 3
            ? new IntRange(Math.Min(numbers[1], numbers[2]), Math.Max(numbers[1], numbers[2]))
            : new IntRange(numbers[1], numbers[1]);
        result = new SkillRange(numbers[0], range, isWeight);
        return true;
    }

    private static TraitFilter ParseTraits(string value)
    {
        var result = new TraitFilter();
        foreach (var raw in SplitNonEmpty(value, '/'))
        {
            switch (raw.Trim().ToUpperInvariant())
            {
                case "M":
                case "-F":
                    result.Female = false;
                    break;
                case "F":
                case "-M":
                    result.Female = true;
                    break;
                case "U": result.Unique = true; break;
                case "-U": result.Unique = false; break;
                case "S": result.Summonable = true; break;
                case "-S": result.Summonable = false; break;
                case "C": result.Child = true; break;
                case "-C": result.Child = false; break;
                case "L": result.Leveled = true; break;
                case "-L": result.Leveled = false; break;
                case "T": result.Teammate = true; break;
                case "-T": result.Teammate = false; break;
                case "D": result.StartsDead = true; break;
                case "-D": result.StartsDead = false; break;
            }
        }

        return result;
    }

    private static ChanceFilter ParseChance(string value, string path, int lineNumber, Action<string> warn)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ChanceFilter.Always;
        }

        var deterministic = value.EndsWith('!');
        var numeric = deterministic ? value[..^1] : value;
        if (!double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            warn($"{Path.GetFileName(path)}:{lineNumber}: invalid chance '{value}'. Using 100%.");
            return ChanceFilter.Always;
        }

        return new ChanceFilter(Math.Clamp(percent, 0.0, 100.0), deterministic);
    }

    private static bool TryParseRange(string token, char separator, out IntRange range)
    {
        range = default!;
        var parts = token.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exact))
        {
            range = new IntRange(exact, exact);
            return true;
        }

        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minimum) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maximum))
        {
            range = new IntRange(Math.Min(minimum, maximum), Math.Max(minimum, maximum));
            return true;
        }

        return false;
    }

    private static IEnumerable<string> SplitNonEmpty(string value, char separator)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (var part in value.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsNoneToken(part)) yield return part;
        }
    }
}
