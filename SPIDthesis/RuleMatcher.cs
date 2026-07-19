using System.Security.Cryptography;
using System.Text;

namespace SPIDthesis;

internal static class RuleMatcher
{
    public static bool Matches(ResolvedRule rule, NpcEvaluationState npc)
    {
        if (!MatchesLevel(rule.Source.LevelFilters, npc) ||
            !MatchesNonRaceTraits(rule.Source.Traits, npc))
        {
            return false;
        }

        npc.PrepareForMatching(rule);
        return MatchesStrings(rule.Source.StringFilters, npc.StringCandidates) &&
               MatchesForms(rule.FormFilters, npc) &&
               MatchesChildTrait(rule.Source.Traits, npc);
    }

    public static int SelectItemCount(SpidRule rule, string npcFormKey, int randomSeed)
    {
        int minimum = Math.Max(0, rule.Count.Minimum);
        int maximum = Math.Max(0, rule.Count.Maximum);
        if (maximum <= minimum) return minimum;

        string identity = $"{randomSeed}|{Path.GetFileName(rule.SourcePath)}|{rule.LineNumber}|{rule.RawLine}|{npcFormKey}|item-count";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        ulong value = BitConverter.ToUInt64(hash, 0);
        ulong width = (ulong)((long)maximum - minimum + 1L);
        return minimum + (int)(value % width);
    }

    private static bool MatchesStrings(TextFilterSet filters, HashSet<string> candidates)
    {
        if (filters.Not.Any(filter => candidates.Contains(filter))) return false;
        if (filters.All.Any(filter => !candidates.Contains(filter))) return false;

        if (filters.Match.Count > 0 && !filters.Match.Any(candidates.Contains)) return false;

        if (filters.Partial.Count > 0)
        {
            bool partialMatch = filters.Partial.Any(filter =>
                candidates.Any(candidate => candidate.Contains(filter, StringComparison.OrdinalIgnoreCase)));
            if (!partialMatch) return false;
        }

        return true;
    }

    private static bool MatchesForms(ResolvedFormFilterSet filters, NpcEvaluationState npc)
    {
        if (filters.Not.Any(npc.MatchesForm)) return false;
        if (filters.All.Any(filter => !npc.MatchesForm(filter))) return false;
        if (filters.Match.Count > 0 && !filters.Match.Any(npc.MatchesForm)) return false;
        return true;
    }

    private static bool MatchesLevel(LevelFilter filter, NpcEvaluationState npc)
    {
        if (filter.ActorLevel is not null && !filter.ActorLevel.Contains(npc.ActorLevel)) return false;

        if (filter.Skills.Count > 0) return false;

        return true;
    }

    private static bool MatchesNonRaceTraits(TraitFilter traits, NpcEvaluationState npc)
    {
        if (traits.Female is not null && traits.Female.Value != npc.Female) return false;
        if (traits.Unique is not null && traits.Unique.Value != npc.Unique) return false;
        if (traits.Summonable is not null && traits.Summonable.Value != npc.Summonable) return false;
        if (traits.Leveled is not null && traits.Leveled.Value != npc.Leveled) return false;
        if (traits.Teammate == true) return false;
        if (traits.StartsDead == true) return false;
        return true;
    }

    private static bool MatchesChildTrait(TraitFilter traits, NpcEvaluationState npc)
    {
        return traits.Child is null || traits.Child.Value == npc.Child;
    }


}
