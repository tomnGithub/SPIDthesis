using Mutagen.Bethesda.Plugins;

namespace SPIDThesis;

internal static class KeywordRuleOrdering
{
    public static IReadOnlyList<ResolvedRule> OrderLikeSpid(
        IReadOnlyList<ResolvedRule> rules,
        RecordIndex index,
        Action<string> warn)
    {
        var keywordRules = rules
            .Where(x => x.Source.Kind == DistributionKind.Keyword)
            .OrderBy(x => x.OriginalOrder)
            .ToArray();

        var ordered = new List<ResolvedRule>(rules.Count);
        ordered.AddRange(OrderKeywords(keywordRules, index, warn));

        // SPID mutates the NPC keyword set before evaluating perks and spells.
        ordered.AddRange(rules.Where(x => x.Source.Kind == DistributionKind.Perk).OrderBy(x => x.OriginalOrder));
        ordered.AddRange(rules.Where(x => x.Source.Kind == DistributionKind.Spell).OrderBy(x => x.OriginalOrder));
        return ordered;
    }

    private static IEnumerable<ResolvedRule> OrderKeywords(
        IReadOnlyList<ResolvedRule> keywordRules,
        RecordIndex index,
        Action<string> warn)
    {
        if (keywordRules.Count <= 1) return keywordRules;

        var groups = keywordRules
            .GroupBy(x => x.DistributedForm)
            .Select(group => new KeywordGroup(
                group.Key,
                index.GetEditorId(group.Key),
                group.OrderBy(x => x.OriginalOrder).ToArray(),
                group.Min(x => x.OriginalOrder)))
            .OrderBy(x => x.FirstOrder)
            .ToArray();

        var byForm = groups.ToDictionary(x => x.FormKey);
        var byEditorId = groups
            .Where(x => !string.IsNullOrWhiteSpace(x.EditorId))
            .GroupBy(x => x.EditorId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(y => y.FormKey).ToArray(), StringComparer.OrdinalIgnoreCase);

        var outgoing = groups.ToDictionary(x => x.FormKey, _ => new HashSet<FormKey>());
        var indegree = groups.ToDictionary(x => x.FormKey, _ => 0);

        foreach (var group in groups)
        {
            foreach (var dependency in FindDependencies(group, groups, byEditorId))
            {
                if (dependency == group.FormKey) continue;
                if (outgoing[dependency].Add(group.FormKey)) indegree[group.FormKey]++;
            }
        }

        var ready = new List<KeywordGroup>(groups.Where(x => indegree[x.FormKey] == 0));
        SortReady(ready);
        var result = new List<ResolvedRule>(keywordRules.Count);
        var emitted = new HashSet<FormKey>();

        while (ready.Count > 0)
        {
            var group = ready[0];
            ready.RemoveAt(0);
            emitted.Add(group.FormKey);
            result.AddRange(group.Rules);

            foreach (var dependent in outgoing[group.FormKey])
            {
                indegree[dependent]--;
                if (indegree[dependent] == 0)
                {
                    ready.Add(byForm[dependent]);
                    SortReady(ready);
                }
            }
        }

        if (emitted.Count != groups.Length)
        {
            warn("Keyword dependency cycle detected. Cyclic keyword groups retain their original INI order.");
            foreach (var group in groups.Where(x => !emitted.Contains(x.FormKey)).OrderBy(x => x.FirstOrder))
            {
                result.AddRange(group.Rules);
            }
        }

        return result;
    }

    private static IEnumerable<FormKey> FindDependencies(
        KeywordGroup group,
        IReadOnlyList<KeywordGroup> allGroups,
        IReadOnlyDictionary<string, FormKey[]> byEditorId)
    {
        var dependencies = new HashSet<FormKey>();
        foreach (var rule in group.Rules)
        {
            AddExact(rule.Source.StringFilters.Match);
            AddExact(rule.Source.StringFilters.All);
            AddExact(rule.Source.StringFilters.Not);

            foreach (var partial in rule.Source.StringFilters.Partial)
            {
                foreach (var candidate in allGroups)
                {
                    if (candidate.EditorId?.Contains(partial, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        dependencies.Add(candidate.FormKey);
                    }
                }
            }
        }

        return dependencies;

        void AddExact(IEnumerable<string> filters)
        {
            foreach (var filter in filters)
            {
                if (!byEditorId.TryGetValue(filter, out var forms)) continue;
                foreach (var form in forms) dependencies.Add(form);
            }
        }
    }

    private static void SortReady(List<KeywordGroup> ready)
    {
        ready.Sort((left, right) =>
        {
            int order = left.FirstOrder.CompareTo(right.FirstOrder);
            if (order != 0) return order;
            return StringComparer.OrdinalIgnoreCase.Compare(left.EditorId ?? string.Empty, right.EditorId ?? string.Empty);
        });
    }

    private sealed record KeywordGroup(
        FormKey FormKey,
        string? EditorId,
        IReadOnlyList<ResolvedRule> Rules,
        int FirstOrder);
}
