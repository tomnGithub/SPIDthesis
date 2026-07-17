using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace SPIDSynthesisPatcher;

internal sealed class NpcEvaluationState
{
    private readonly IPatcherState<ISkyrimMod, ISkyrimModGetter> _state;
    private readonly RecordIndex _index;

    public INpcGetter Source { get; }
    public List<INpcGetter> TemplateChain { get; } = new();
    public List<INpcGetter> MatchNpcSources { get; } = new();
    public HashSet<string> StringCandidates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> SourcePlugins { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<FormKey> RelatedForms { get; } = new();
    public HashSet<FormKey> Keywords { get; } = new();
    public HashSet<FormKey> Spells { get; } = new();
    public Dictionary<FormKey, byte> Perks { get; } = new();

    public int ActorLevel { get; private set; }
    public bool Female { get; private set; }
    public bool Unique { get; private set; }
    public bool Summonable { get; private set; }
    public bool Child { get; private set; }
    public bool Leveled { get; private set; }

    public bool CanMaterializeKeywords { get; private set; } = true;
    public bool CanMaterializeSpellList { get; private set; } = true;
    public bool KeywordsMaterialized { get; set; }
    public bool SpellListMaterialized { get; set; }

    public bool InheritsKeywords => Source.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Keywords);
    public bool InheritsSpellList => Source.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.SpellList);

    private NpcEvaluationState(
        IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
        RecordIndex index,
        INpcGetter source)
    {
        _state = state;
        _index = index;
        Source = source;
    }

    public static NpcEvaluationState Create(
        IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
        RecordIndex index,
        INpcGetter source)
    {
        var result = new NpcEvaluationState(state, index, source);
        result.Build();
        return result;
    }

    private void Build()
    {
        BuildTemplateChain();
        BuildMatchNpcSources();
        BuildEffectiveLists();
        BuildStaticFields();
    }

    private void BuildTemplateChain()
    {
        var visited = new HashSet<FormKey>();
        INpcGetter current = Source;

        while (visited.Add(current.FormKey))
        {
            TemplateChain.Add(current);
            if (current.Template.IsNull) break;

            try
            {
                var spawn = current.Template.TryResolve(_state.LinkCache);
                if (spawn is not INpcGetter next) break;
                current = next;
            }
            catch
            {
                break;
            }
        }
    }

    private void BuildMatchNpcSources()
    {
        var visitedNpcs = new HashSet<FormKey>();
        var visitedLeveledLists = new HashSet<FormKey>();
        AddNpcAndTemplateBranches(Source, visitedNpcs, visitedLeveledLists);
    }

    private void AddNpcAndTemplateBranches(
        INpcGetter npc,
        HashSet<FormKey> visitedNpcs,
        HashSet<FormKey> visitedLeveledLists)
    {
        if (!visitedNpcs.Add(npc.FormKey)) return;
        MatchNpcSources.Add(npc);
        if (npc.Template.IsNull) return;

        try
        {
            AddSpawnAndTemplateBranches(
                npc.Template.TryResolve(_state.LinkCache),
                visitedNpcs,
                visitedLeveledLists);
        }
        catch
        {
            // An unresolved template contributes no additional static match data.
        }
    }

    private void AddSpawnAndTemplateBranches(
        INpcSpawnGetter? spawn,
        HashSet<FormKey> visitedNpcs,
        HashSet<FormKey> visitedLeveledLists)
    {
        switch (spawn)
        {
            case INpcGetter npc:
                AddNpcAndTemplateBranches(npc, visitedNpcs, visitedLeveledLists);
                break;

            case ILeveledNpcGetter leveledNpc:
                AddLeveledNpcBranches(leveledNpc, visitedNpcs, visitedLeveledLists);
                break;
        }
    }

    private void AddLeveledNpcBranches(
        ILeveledNpcGetter leveledNpc,
        HashSet<FormKey> visitedNpcs,
        HashSet<FormKey> visitedLeveledLists)
    {
        if (!visitedLeveledLists.Add(leveledNpc.FormKey)) return;

        foreach (var entry in leveledNpc.Entries ?? Array.Empty<ILeveledNpcEntryGetter>())
        {
            var reference = entry.Data?.Reference;
            if (reference is null || reference.IsNull) continue;

            try
            {
                AddSpawnAndTemplateBranches(
                    reference.TryResolve(_state.LinkCache),
                    visitedNpcs,
                    visitedLeveledLists);
            }
            catch
            {
                // Ignore individual unresolved LVLN entries and keep examining the rest.
            }
        }
    }

    private void BuildEffectiveLists()
    {
        if (!TryGetEffectiveKeywordProvider(Source, new HashSet<FormKey>(), out var keywordProvider))
        {
            CanMaterializeKeywords = false;
            keywordProvider = Source;
        }

        foreach (var keyword in keywordProvider.Keywords ?? Array.Empty<Mutagen.Bethesda.Plugins.IFormLinkGetter<IKeywordGetter>>())
        {
            Keywords.Add(keyword.FormKey);
        }

        if (!TryGetEffectiveSpellListProvider(Source, new HashSet<FormKey>(), out var spellProvider))
        {
            CanMaterializeSpellList = false;
            spellProvider = Source;
        }

        foreach (var spell in spellProvider.ActorEffect ?? Array.Empty<Mutagen.Bethesda.Plugins.IFormLinkGetter<ISpellRecordGetter>>())
        {
            Spells.Add(spell.FormKey);
        }

        foreach (var perk in spellProvider.Perks ?? Array.Empty<IPerkPlacementGetter>())
        {
            Perks[perk.Perk.FormKey] = perk.Rank;
        }
    }

    private bool TryGetEffectiveKeywordProvider(INpcGetter npc, HashSet<FormKey> visited, out INpcGetter provider)
    {
        provider = npc;
        if (!visited.Add(npc.FormKey)) return false;
        if (!npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Keywords)) return true;
        if (npc.Template.IsNull) return false;

        try
        {
            var spawn = npc.Template.TryResolve(_state.LinkCache);
            return spawn is INpcGetter templateNpc && TryGetEffectiveKeywordProvider(templateNpc, visited, out provider);
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetEffectiveSpellListProvider(INpcGetter npc, HashSet<FormKey> visited, out INpcGetter provider)
    {
        provider = npc;
        if (!visited.Add(npc.FormKey)) return false;
        if (!npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.SpellList)) return true;
        if (npc.Template.IsNull) return false;

        try
        {
            var spawn = npc.Template.TryResolve(_state.LinkCache);
            return spawn is INpcGetter templateNpc && TryGetEffectiveSpellListProvider(templateNpc, visited, out provider);
        }
        catch
        {
            return false;
        }
    }

    private void BuildStaticFields()
    {
        ActorLevel = GetStaticActorLevel(Source);
        Female = EnumFlagHelpers.HasNamedFlag(Source.Configuration.Flags, "Female");
        Unique = EnumFlagHelpers.HasNamedFlag(Source.Configuration.Flags, "Unique");
        Summonable = EnumFlagHelpers.HasNamedFlag(Source.Configuration.Flags, "Summonable");
        Leveled = IsLeveledTemplate(Source);

        // SPID evaluates a live actor and uses TESNPC::IsInFaction. For a statically patched NPC whose
        // template resolves through one or more LVLN records, any reachable concrete NPC can supply the
        // effective faction/trait forms. Use the union of all reachable branches for positive matching.
        foreach (var npc in MatchNpcSources)
        {
            RelatedForms.Add(npc.FormKey);
            SourcePlugins.Add(npc.FormKey.ModKey.FileName.String);
            AddString(npc.EditorID);
            AddString(npc.Name?.String);

            AddLink(npc.Race.FormKey);
            AddLink(npc.Class.FormKey);
            AddLink(npc.CombatStyle.FormKey);
            AddLink(npc.Voice.FormKey);
            AddLink(npc.DefaultOutfit.FormKey);
            AddLink(npc.SleepingOutfit.FormKey);
            AddLink(npc.CrimeFaction.FormKey);

            foreach (var faction in npc.Factions ?? Array.Empty<IRankPlacementGetter>())
            {
                AddLink(faction.Faction.FormKey);
            }
        }

        foreach (var keyword in Keywords)
        {
            AddString(_index.GetEditorId(keyword));
        }

        foreach (var spell in Spells) RelatedForms.Add(spell);
        foreach (var perk in Perks.Keys) RelatedForms.Add(perk);

        foreach (var raceKey in MatchNpcSources.Select(x => x.Race.FormKey).Where(x => !x.IsNull).Distinct())
        {
            if (!_index.Races.TryGetValue(raceKey, out var race)) continue;
            if (EnumFlagHelpers.HasNamedFlag(race.Flags, "Child")) Child = true;
            foreach (var keyword in race.Keywords ?? Array.Empty<Mutagen.Bethesda.Plugins.IFormLinkGetter<IKeywordGetter>>())
            {
                AddString(_index.GetEditorId(keyword.FormKey));
            }
        }
    }

    private void AddLink(FormKey key)
    {
        if (!key.IsNull) RelatedForms.Add(key);
    }

    private void AddString(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) StringCandidates.Add(value);
    }

    private bool IsLeveledTemplate(INpcGetter npc)
    {
        if (npc.Template.IsNull) return false;
        try
        {
            return npc.Template.TryResolve(_state.LinkCache) is ILeveledNpcGetter;
        }
        catch
        {
            return false;
        }
    }

    private static int GetStaticActorLevel(INpcGetter npc)
    {
        // Avoid coupling to a specific generated ANpcLevel implementation: fixed levels expose a Level property.
        object levelObject = npc.Configuration.Level;
        PropertyInfo? property = levelObject.GetType().GetProperty("Level", BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(levelObject) is IConvertible convertible)
        {
            try
            {
                return convertible.ToInt32(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                // Fall through to the autocalc minimum.
            }
        }

        return npc.Configuration.CalcMinLevel;
    }


    public void RegisterKeyword(FormKey keyword)
    {
        Keywords.Add(keyword);
        AddString(_index.GetEditorId(keyword));
    }

    public bool MatchesForm(ResolvedFormFilter filter)
    {
        if (filter.IsPlugin)
        {
            return filter.PluginName is not null && SourcePlugins.Contains(filter.PluginName);
        }

        foreach (var candidate in filter.Candidates)
        {
            if (candidate.Kind == IndexedFormKind.FormList)
            {
                if (MatchesFormList(candidate.FormKey, new HashSet<FormKey>())) return true;
            }
            else if (RelatedForms.Contains(candidate.FormKey))
            {
                return true;
            }
        }

        return false;
    }

    private bool MatchesFormList(FormKey formList, HashSet<FormKey> visited)
    {
        if (!visited.Add(formList)) return false;
        if (!_index.FormListItems.TryGetValue(formList, out var items)) return false;

        foreach (var item in items)
        {
            if (RelatedForms.Contains(item)) return true;
            if (_index.FormListItems.ContainsKey(item) && MatchesFormList(item, visited)) return true;
        }

        return false;
    }
}
