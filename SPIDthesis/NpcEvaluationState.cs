using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace SPIDThesis;

internal sealed class NpcEvaluationState
{
    private readonly RecordIndex _index;

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
    private readonly FormKey[] _packageLists = new FormKey[5];

    public int ActorLevel { get; private set; }
    public bool Female { get; private set; }
    public bool Unique { get; private set; }
    public bool Summonable { get; private set; }
    public bool Child { get; private set; }
    public bool Leveled => false;

    private NpcEvaluationState(RecordIndex index, INpcGetter source)
    {
        _index = index;
        Source = source;
    }

    public static NpcEvaluationState Create(RecordIndex index, INpcGetter source)
    {
        var result = new NpcEvaluationState(index, source);
        result.Build();
        return result;
    }

    private void Build()
    {
        BuildDirectLists();
        BuildStaticFields();
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

        AddLink(Source.Race.FormKey);
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

        var raceKey = Source.Race.FormKey;
        if (!raceKey.IsNull && _index.Races.TryGetValue(raceKey, out var race))
        {
            Child = EnumFlagHelpers.HasNamedFlag(race.Flags, "Child");
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
