using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace SPIDthesis;

internal sealed class NpcEvaluationState
{
    private static readonly FormKey FoxRaceFormKey = FormKey.Factory("109C7C:Skyrim.esm");

    private readonly IPatcherState<ISkyrimMod, ISkyrimModGetter> _state;
    private readonly RecordIndex _index;
    private bool _templateRaceResolved;
    private bool _raceDataBuilt;

    public INpcGetter Source { get; }
    public HashSet<string> StringCandidates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> SourcePlugins { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<FormKey> RelatedForms { get; } = new();
    public HashSet<FormKey> Keywords { get; } = new();
    public HashSet<FormKey> Spells { get; } = new();
    public HashSet<FormKey> Shouts { get; } = new();
    public HashSet<FormKey> Factions { get; } = new();
    public HashSet<FormKey> Packages { get; } = new();
    public Dictionary<FormKey, byte> Perks { get; } = new();
    public Dictionary<FormKey, int> Items { get; } = new();
    public FormKey DefaultOutfit { get; private set; }
    public FormKey SleepingOutfit { get; private set; }
    public FormKey Skin { get; private set; }
    public FormKey EffectiveRace { get; private set; }
    public bool IsFoxTemplatePlaceholder { get; private set; }
    private readonly FormKey[] _packageLists = new FormKey[5];

    public int ActorLevel { get; private set; }
    public bool Female { get; private set; }
    public bool Unique { get; private set; }
    public bool Summonable { get; private set; }
    public bool Child { get; private set; }
    public bool Leveled => false;

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
        BuildDirectLists();
        BuildFoxTemplateState();
        BuildStaticFields();
        if (!IsFoxTemplatePlaceholder) BuildRaceData();
    }

    private void BuildFoxTemplateState()
    {
        EffectiveRace = Source.Race.FormKey;
        IsFoxTemplatePlaceholder = IsFoxRace(EffectiveRace) && !Source.Template.IsNull;
    }

    public void PrepareForMatching(ResolvedRule rule)
    {
        if (!IsFoxTemplatePlaceholder || _raceDataBuilt || !RequiresEffectiveRace(rule)) return;
        ResolveFoxTemplateRace();
    }

    private static bool RequiresEffectiveRace(ResolvedRule rule)
    {
        if (!rule.Source.StringFilters.IsEmpty || rule.Source.Traits.Child is not null) return true;
        return HasRaceSensitiveFilter(rule.FormFilters.Match) ||
               HasRaceSensitiveFilter(rule.FormFilters.All) ||
               HasRaceSensitiveFilter(rule.FormFilters.Not);
    }

    private static bool HasRaceSensitiveFilter(IEnumerable<ResolvedFormFilter> filters)
    {
        return filters.Any(filter => filter.Candidates.Any(candidate =>
            candidate.Kind is IndexedFormKind.Race or IndexedFormKind.FormList));
    }

    private void ResolveFoxTemplateRace()
    {
        if (_templateRaceResolved) return;
        _templateRaceResolved = true;
        var resolvedRace = FindFirstNonFoxTemplateRace(
            Source,
            new HashSet<FormKey>(),
            new HashSet<FormKey>());
        if (!resolvedRace.IsNull) EffectiveRace = resolvedRace;
        BuildRaceData();
    }

    private FormKey FindFirstNonFoxTemplateRace(
        INpcGetter npc,
        HashSet<FormKey> visitedNpcs,
        HashSet<FormKey> visitedLeveledNpcs)
    {
        if (!visitedNpcs.Add(npc.FormKey) || npc.Template.IsNull) return default;

        try
        {
            return FindFirstNonFoxRace(
                npc.Template.TryResolve(_state.LinkCache),
                visitedNpcs,
                visitedLeveledNpcs);
        }
        catch
        {
            return default;
        }
    }

    private FormKey FindFirstNonFoxRace(
        INpcSpawnGetter? spawn,
        HashSet<FormKey> visitedNpcs,
        HashSet<FormKey> visitedLeveledNpcs)
    {
        switch (spawn)
        {
            case INpcGetter npc:
                if (!visitedNpcs.Add(npc.FormKey)) return default;
                var race = npc.Race.FormKey;
                if (!race.IsNull && !IsFoxRace(race)) return race;
                if (npc.Template.IsNull) return default;
                try
                {
                    return FindFirstNonFoxRace(
                        npc.Template.TryResolve(_state.LinkCache),
                        visitedNpcs,
                        visitedLeveledNpcs);
                }
                catch
                {
                    return default;
                }

            case ILeveledNpcGetter leveledNpc:
                if (!visitedLeveledNpcs.Add(leveledNpc.FormKey)) return default;
                foreach (var entry in leveledNpc.Entries ?? Array.Empty<ILeveledNpcEntryGetter>())
                {
                    var reference = entry.Data?.Reference;
                    if (reference is null || reference.IsNull) continue;
                    try
                    {
                        var found = FindFirstNonFoxRace(
                            reference.TryResolve(_state.LinkCache),
                            visitedNpcs,
                            visitedLeveledNpcs);
                        if (!found.IsNull) return found;
                    }
                    catch
                    {
                    }
                }
                break;
        }

        return default;
    }

    private bool IsFoxRace(FormKey race)
    {
        return race == FoxRaceFormKey ||
               string.Equals(_index.GetEditorId(race), "FoxRace", StringComparison.OrdinalIgnoreCase);
    }

    public bool CanDistribute(ResolvedRule rule)
    {
        if (!IsFoxTemplatePlaceholder) return true;

        return rule.Source.Kind switch
        {
            DistributionKind.Keyword => !HasTemplateFlag("Keywords"),
            DistributionKind.Spell => !HasTemplateFlag("SpellList"),
            DistributionKind.Perk => !HasTemplateFlag("SpellList"),
            DistributionKind.Shout => !HasTemplateFlag("SpellList"),
            DistributionKind.Faction => !HasTemplateFlag("Factions"),
            DistributionKind.Package when rule.DistributedKind == IndexedFormKind.FormList &&
                                          rule.Source.PackageIndex == 0 =>
                !HasTemplateFlag("DefaultPackageList", "DefaultPackList", "DefPackList", "AIPackages", "AiPackages", "Packages"),
            DistributionKind.Package => !HasTemplateFlag("AIPackages", "AiPackages", "Packages"),
            DistributionKind.Item => !HasTemplateFlag("Inventory"),
            DistributionKind.Outfit => !HasTemplateFlag("Inventory"),
            DistributionKind.SleepOutfit => !HasTemplateFlag("Inventory"),
            DistributionKind.Skin => !HasTemplateFlag("Traits"),
            _ => true
        };
    }

    private bool HasTemplateFlag(params string[] names)
    {
        return names.Any(name =>
            EnumFlagHelpers.HasNamedFlag(Source.Configuration.TemplateFlags, name));
    }

    private void BuildDirectLists()
    {
        foreach (var keyword in Source.Keywords ?? Array.Empty<Mutagen.Bethesda.Plugins.IFormLinkGetter<IKeywordGetter>>())
        {
            Keywords.Add(keyword.FormKey);
        }

        foreach (var spell in Source.ActorEffect ?? Array.Empty<Mutagen.Bethesda.Plugins.IFormLinkGetter<ISpellRecordGetter>>())
        {
            if (_index.Shouts.ContainsKey(spell.FormKey))
            {
                Shouts.Add(spell.FormKey);
            }
            else
            {
                Spells.Add(spell.FormKey);
            }
        }

        foreach (var faction in Source.Factions ?? Array.Empty<IRankPlacementGetter>())
        {
            if (!faction.Faction.FormKey.IsNull) Factions.Add(faction.Faction.FormKey);
        }

        foreach (var package in Source.Packages)
        {
            if (!package.FormKey.IsNull) Packages.Add(package.FormKey);
        }

        foreach (var perk in Source.Perks ?? Array.Empty<IPerkPlacementGetter>())
        {
            Perks[perk.Perk.FormKey] = perk.Rank;
        }

        foreach (var entry in Source.Items ?? Array.Empty<IContainerEntryGetter>())
        {
            var key = entry.Item.Item.FormKey;
            if (key.IsNull) continue;
            Items[key] = Items.TryGetValue(key, out var existing)
                ? existing + entry.Item.Count
                : entry.Item.Count;
        }

        DefaultOutfit = Source.DefaultOutfit.FormKey;
        SleepingOutfit = Source.SleepingOutfit.FormKey;
        Skin = Source.WornArmor.FormKey;
        _packageLists[0] = Source.DefaultPackageList.FormKey;
        _packageLists[1] = Source.SpectatorOverridePackageList.FormKey;
        _packageLists[2] = Source.ObserveDeadBodyOverridePackageList.FormKey;
        _packageLists[3] = Source.GuardWarnOverridePackageList.FormKey;
        _packageLists[4] = Source.CombatOverridePackageList.FormKey;
    }

    private void BuildStaticFields()
    {
        ActorLevel = GetStaticActorLevel(Source);
        Female = EnumFlagHelpers.HasNamedFlag(Source.Configuration.Flags, "Female");
        Unique = EnumFlagHelpers.HasNamedFlag(Source.Configuration.Flags, "Unique");
        Summonable = EnumFlagHelpers.HasNamedFlag(Source.Configuration.Flags, "Summonable");

        RelatedForms.Add(Source.FormKey);
        SourcePlugins.Add(Source.FormKey.ModKey.FileName.String);
        AddString(Source.EditorID);
        AddString(Source.Name?.String);

        AddLink(Source.Class.FormKey);
        AddLink(Source.CombatStyle.FormKey);
        AddLink(Source.Voice.FormKey);
        AddLink(Source.DefaultOutfit.FormKey);
        AddLink(Source.SleepingOutfit.FormKey);
        AddLink(Source.CrimeFaction.FormKey);

        foreach (var faction in Factions) AddLink(faction);

        foreach (var keyword in Keywords)
        {
            AddString(_index.GetEditorId(keyword));
        }

        foreach (var spell in Spells) RelatedForms.Add(spell);
        foreach (var shout in Shouts) RelatedForms.Add(shout);
        foreach (var package in Packages) RelatedForms.Add(package);
        foreach (var packageList in _packageLists) AddLink(packageList);
        foreach (var perk in Perks.Keys) RelatedForms.Add(perk);
        foreach (var item in Items.Keys) RelatedForms.Add(item);
        AddLink(DefaultOutfit);
        AddLink(SleepingOutfit);
        AddLink(Skin);
    }

    private void BuildRaceData()
    {
        if (_raceDataBuilt) return;
        _raceDataBuilt = true;
        AddLink(EffectiveRace);
        if (EffectiveRace.IsNull || !_index.Races.TryGetValue(EffectiveRace, out var race)) return;
        Child = EnumFlagHelpers.HasNamedFlag(race.Flags, "Child");
        foreach (var keyword in race.Keywords ?? Array.Empty<Mutagen.Bethesda.Plugins.IFormLinkGetter<IKeywordGetter>>())
        {
            AddString(_index.GetEditorId(keyword.FormKey));
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

    private static int GetStaticActorLevel(INpcGetter npc)
    {
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
            }
        }

        return npc.Configuration.CalcMinLevel;
    }

    public void RegisterKeyword(FormKey keyword)
    {
        Keywords.Add(keyword);
        AddString(_index.GetEditorId(keyword));
    }

    public void RegisterShout(FormKey shout)
    {
        Shouts.Add(shout);
        RelatedForms.Add(shout);
    }

    public void RegisterFaction(FormKey faction)
    {
        Factions.Add(faction);
        RelatedForms.Add(faction);
    }

    public void RegisterPackage(FormKey package)
    {
        Packages.Add(package);
        RelatedForms.Add(package);
    }

    public FormKey GetPackageList(int index)
    {
        return index is >= 0 and < 5 ? _packageLists[index] : default;
    }

    public void RegisterPackageList(int index, FormKey formList)
    {
        if (index is < 0 or >= 5) return;
        _packageLists[index] = formList;
        RelatedForms.Add(formList);
    }

    public void RegisterItem(FormKey item, int count)
    {
        Items[item] = Items.TryGetValue(item, out var existing) ? existing + count : count;
        RelatedForms.Add(item);
    }

    public void RegisterOutfit(FormKey outfit)
    {
        DefaultOutfit = outfit;
        RelatedForms.Add(outfit);
    }

    public void RegisterSleepOutfit(FormKey outfit)
    {
        SleepingOutfit = outfit;
        RelatedForms.Add(outfit);
    }

    public void RegisterSkin(FormKey armor)
    {
        Skin = armor;
        RelatedForms.Add(armor);
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
