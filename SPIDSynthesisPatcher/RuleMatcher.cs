using System.Security.Cryptography;
using System.Text;

namespace SPIDSynthesisPatcher;

internal static class RuleMatcher
{
    public static bool Matches(ResolvedRule rule, NpcEvaluationState npc, Settings settings)
    {
        return MatchesStrings(rule.Source.StringFilters, npc.StringCandidates) &&
               MatchesForms(rule.FormFilters, npc) &&
               MatchesLevel(rule.Source.LevelFilters, npc) &&
               MatchesTraits(rule.Source.Traits, npc) &&
               PassesChance(rule.Source, npc.Source.FormKey.ToString(), settings.RandomSeed);
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

        // SPID evaluates skill values/weights on live actors. A Synthesis patch has no reliable equivalent
        // for every NPC/template combination, so rules using skill filters fail closed instead of over-distributing.
        if (filter.Skills.Count > 0) return false;

        return true;
    }

    private static bool MatchesTraits(TraitFilter traits, NpcEvaluationState npc)
    {
        if (traits.Female is not null && traits.Female.Value != npc.Female) return false;
        if (traits.Unique is not null && traits.Unique.Value != npc.Unique) return false;
        if (traits.Summonable is not null && traits.Summonable.Value != npc.Summonable) return false;
        if (traits.Child is not null && traits.Child.Value != npc.Child) return false;
        if (traits.Leveled is not null && traits.Leveled.Value != npc.Leveled) return false;

        // Teammate and starts-dead are actor-reference/runtime states. Positive requirements fail closed;
        // negative requirements pass because a base NPC record cannot prove the runtime state.
        if (traits.Teammate == true) return false;
        if (traits.StartsDead == true) return false;

        return true;
    }

    private static bool PassesChance(SpidRule rule, string npcFormKey, int randomSeed)
    {
        if (rule.Chance.Percent >= 100.0) return true;
        if (rule.Chance.Percent <= 0.0) return false;

        string seed = rule.Chance.Deterministic ? string.Empty : randomSeed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string identity = $"{seed}|{Path.GetFileName(rule.SourcePath)}|{rule.LineNumber}|{rule.RawLine}|{npcFormKey}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        ulong value = BitConverter.ToUInt64(hash, 0);
        double roll = value / (double)ulong.MaxValue * 100.0;
        return roll < rule.Chance.Percent;
    }
}
