using Mutagen.Bethesda.Plugins;

namespace SPIDthesis;

internal enum DistributionKind
{
    Keyword,
    Spell,
    Perk,
    Shout,
    Package,
    Item,
    Outfit,
    SleepOutfit,
    Faction,
    Skin
}

internal enum IndexedFormKind
{
    Unknown,
    Npc,
    Race,
    Keyword,
    Faction,
    Class,
    CombatStyle,
    VoiceType,
    Outfit,
    Spell,
    Perk,
    Shout,
    Package,
    Armor,
    Item,
    FormList
}

internal sealed record IndexedForm(FormKey FormKey, string? EditorId, IndexedFormKind Kind);

internal sealed class TextFilterSet
{
    public List<string> Match { get; } = new();
    public List<string> All { get; } = new();
    public List<string> Not { get; } = new();
    public List<string> Partial { get; } = new();

    public bool IsEmpty => Match.Count == 0 && All.Count == 0 && Not.Count == 0 && Partial.Count == 0;
}

internal sealed class RawFormFilterSet
{
    public List<string> Match { get; } = new();
    public List<string> All { get; } = new();
    public List<string> Not { get; } = new();

    public bool IsEmpty => Match.Count == 0 && All.Count == 0 && Not.Count == 0;
}

internal sealed record IntRange(int Minimum, int Maximum)
{
    public bool Contains(int value) => value >= Minimum && value <= Maximum;
}

internal sealed record SkillRange(int SkillIndex, IntRange Range, bool IsWeight);

internal sealed class LevelFilter
{
    public IntRange? ActorLevel { get; set; }
    public List<SkillRange> Skills { get; } = new();
    public bool IsEmpty => ActorLevel is null && Skills.Count == 0;
}

internal sealed class TraitFilter
{
    public bool? Female { get; set; }
    public bool? Unique { get; set; }
    public bool? Summonable { get; set; }
    public bool? Child { get; set; }
    public bool? Leveled { get; set; }
    public bool? Teammate { get; set; }
    public bool? StartsDead { get; set; }

    public bool IsEmpty => Female is null && Unique is null && Summonable is null && Child is null &&
                           Leveled is null && Teammate is null && StartsDead is null;
}

internal sealed record ChanceFilter(double Percent, bool Deterministic)
{
    public static readonly ChanceFilter Always = new(100.0, false);
}

internal sealed class SpidRule
{
    public DistributionKind Kind { get; init; }
    public string RawDistributedForm { get; init; } = string.Empty;
    public TextFilterSet StringFilters { get; init; } = new();
    public RawFormFilterSet FormFilters { get; init; } = new();
    public LevelFilter LevelFilters { get; init; } = new();
    public TraitFilter Traits { get; init; } = new();
    public IntRange Count { get; init; } = new(1, 1);
    public int PackageIndex { get; init; }
    public ChanceFilter Chance { get; init; } = ChanceFilter.Always;
    public string SourcePath { get; init; } = string.Empty;
    public int LineNumber { get; init; }
    public string RawLine { get; init; } = string.Empty;
    public bool HadUnsupportedFinalPrefix { get; init; }
    public bool IsFinalOutfit { get; init; }
}

internal sealed record SpidParseResult(
    IReadOnlyList<SpidRule> Rules,
    IReadOnlyList<string> ProcessedFiles);

internal sealed class ResolvedFormFilter
{
    public string Raw { get; init; } = string.Empty;
    public bool IsPlugin { get; init; }
    public string? PluginName { get; init; }
    public List<IndexedForm> Candidates { get; } = new();
    public bool IsResolved => IsPlugin || Candidates.Count > 0;
}

internal sealed class ResolvedFormFilterSet
{
    public List<ResolvedFormFilter> Match { get; } = new();
    public List<ResolvedFormFilter> All { get; } = new();
    public List<ResolvedFormFilter> Not { get; } = new();
    public bool IsEmpty => Match.Count == 0 && All.Count == 0 && Not.Count == 0;
}

internal sealed class ResolvedRule
{
    public SpidRule Source { get; init; } = null!;
    public FormKey DistributedForm { get; init; }
    public IndexedFormKind DistributedKind { get; init; }
    public ResolvedFormFilterSet FormFilters { get; init; } = new();
    public int OriginalOrder { get; init; }
}
