using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using Noggog;

namespace SPIDthesis;

internal sealed class SPIDthesisDistributor
{
    private static readonly FormKey PlayerFormKey = FormKey.Factory("000007:Skyrim.esm");

    private readonly IPatcherState<ISkyrimMod, ISkyrimModGetter> _state;
    private readonly Settings _settings;
    private readonly HashSet<string> _includedIniFiles;
    private readonly HashSet<string> _ignoredIniFiles;
    private readonly string _convertedFolderPath;
    private readonly int _runSeed;

    private int _keywordsCreated;
    private int _leveledItemsCreated;
    private int _leveledSpellsCreated;
    private int _chanceRecordsCreated;
    private int _npcsScanned;
    private int _npcsChanged;
    private readonly Dictionary<SpidRule, FormKey> _chanceSpellLists = new();
    private readonly Dictionary<(SpidRule Rule, int Count), FormKey> _chanceItemLists = new();
    private readonly Dictionary<(DistributionKind Kind, FormKey Form), HashSet<FormKey>> _distributedNpcCounts = new();

    public SPIDthesisDistributor(
        IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
        Settings settings)
    {
        _state = state;
        _settings = settings;
        _includedIniFiles = NormalizeIniFileNames(settings.IncludedIniFiles);
        _ignoredIniFiles = NormalizeIniFileNames(settings.IgnoredIniFiles);
        _convertedFolderPath = Path.Combine(state.DataFolderPath.ToString(), "SPIDthesisConverted");
        _runSeed = BitConverter.ToInt32(RandomNumberGenerator.GetBytes(sizeof(int)));
    }

    public void Run()
    {
        SpidLog.Header("INI");

        var iniFiles = DiscoverIniFiles().ToArray();
        if (iniFiles.Length == 0)
        {
            SpidLog.Warn("No .ini files with _DISTR suffix were found within the Data folder, aborting...");
            return;
        }

        SpidLog.Info($"{iniFiles.Length} matching inis found");
        foreach (var iniFile in iniFiles)
        {
            SpidLog.Info($"\tINI : {ToSpidDataPath(iniFile)}");
        }

        var parseResult = SpidIniParser.ParseFiles(iniFiles, Warn);
        var parsedRules = parseResult.Rules;
        var skippedChanceRules = parsedRules
            .Where(IsUnsupportedChanceRule)
            .ToArray();
        var distributionRules = parsedRules
            .Where(rule => !IsUnsupportedChanceRule(rule))
            .ToArray();

        SpidLog.Header("LOOKUP");
        var index = RecordIndex.Build(_state);
        var resolvedRules = ResolveRules(distributionRules, index).ToArray();
        resolvedRules = KeywordRuleOrdering.OrderLikeSpid(resolvedRules, index, Warn).ToArray();

        if (resolvedRules.Length > 0)
        {
            SpidLog.Header("PROCESSING");
            LogRegisteredRules(distributionRules, resolvedRules);
            foreach (var npc in _state.LoadOrder.PriorityOrder.Npc().WinningOverrides())
            {
                if (npc.FormKey == PlayerFormKey) continue;
                _npcsScanned++;

                var evaluation = NpcEvaluationState.Create(_state, index, npc);
                bool changed = false;
                bool outfitRuleSettled = false;
                bool sleepOutfitRuleSettled = false;
                bool skinRuleSettled = false;

                foreach (var rule in resolvedRules)
                {
                    if (!KindEnabled(rule.Source.Kind)) continue;
                    if (rule.Source.Kind == DistributionKind.Outfit && outfitRuleSettled) continue;
                    if (rule.Source.Kind == DistributionKind.SleepOutfit && sleepOutfitRuleSettled) continue;
                    if (rule.Source.Kind == DistributionKind.Skin && skinRuleSettled) continue;
                    if (!evaluation.CanDistribute(rule)) continue;
                    if (!RuleMatcher.Matches(rule, evaluation)) continue;

                    if (rule.Source.Kind == DistributionKind.Outfit) outfitRuleSettled = true;
                    if (rule.Source.Kind == DistributionKind.SleepOutfit) sleepOutfitRuleSettled = true;
                    if (rule.Source.Kind == DistributionKind.Skin) skinRuleSettled = true;

                    if (AlreadyHas(rule, evaluation)) continue;
                    if (Apply(rule, evaluation))
                    {
                        changed = true;
                        RegisterDistributedNpc(rule, npc.FormKey);
                    }
                }

                if (changed) _npcsChanged++;
            }

            LogDistributionSummary(resolvedRules);
        }

        MoveProcessedIniFiles(parseResult.ProcessedFiles);
        LogSkippedChanceLines(skippedChanceRules, index);
    }

    private static bool IsUnsupportedChanceRule(SpidRule rule)
    {
        return rule.HasChanceCondition &&
               rule.Kind != DistributionKind.Item &&
               rule.Kind != DistributionKind.Spell;
    }

    private void LogSkippedChanceLines(
        IReadOnlyCollection<SpidRule> skippedRules,
        RecordIndex index)
    {
        if (skippedRules.Count == 0) return;

        var orderedRules = skippedRules
            .OrderBy(rule => rule.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rule => rule.LineNumber)
            .ToArray();

        SpidLog.Header("SKIPPED CHANCE LINES");
        SpidLog.Info($"{orderedRules.Length} line{(orderedRules.Length == 1 ? string.Empty : "s")} skipped because sub-100% chance is only supported for Items and Spells");

        foreach (var rule in orderedRules)
        {
            string plugin = ResolveSkippedChanceRulePlugin(rule, index);
            SpidLog.Info($"	[{ToSpidDataPath(rule.SourcePath)}:{rule.LineNumber}] [{plugin}] {rule.RawLine.Trim()}");
        }
    }

    private string ResolveSkippedChanceRulePlugin(SpidRule rule, RecordIndex index)
    {
        if (index.TryResolveDistributedForm(rule.Kind, rule.RawDistributedForm, out var formKey, out _))
        {
            return formKey.ModKey.FileName.String;
        }

        if (RecordIndex.TryParseFormKey(rule.RawDistributedForm, out var parsed))
        {
            return parsed.ModKey.FileName.String;
        }

        if (rule.Kind == DistributionKind.Keyword && RecordIndex.IsBareEditorId(rule.RawDistributedForm))
        {
            return _state.PatchMod.ModKey.FileName.String;
        }

        return "unresolved";
    }

    private void LogDistributionSummary(
        IReadOnlyCollection<ResolvedRule> resolvedRules)
    {
        SpidLog.Header("DISTRIBUTION");
        SpidLog.Info($"Patched {_npcsChanged}/{_npcsScanned} NPCs");

        if (_keywordsCreated > 0)
        {
            SpidLog.Info($"Created {_keywordsCreated} Keywords");
        }

        if (_leveledItemsCreated > 0)
        {
            SpidLog.Info($"Created {_leveledItemsCreated} chance Leveled Item records");
        }

        if (_leveledSpellsCreated > 0)
        {
            SpidLog.Info($"Created {_leveledSpellsCreated} chance Leveled Spell records");
        }

        foreach (var kind in Enum.GetValues<DistributionKind>())
        {
            var forms = resolvedRules
                .Where(rule => rule.Source.Kind == kind)
                .Select(rule => rule.DistributedForm)
                .Distinct()
                .ToArray();
            if (forms.Length == 0) continue;

            SpidLog.Info(GetSpidRecordName(kind));
            foreach (var form in forms)
            {
                int count = _distributedNpcCounts.TryGetValue((kind, form), out var npcs)
                    ? npcs.Count
                    : 0;
                SpidLog.Info($"	{FormatSpidForm(form)} added to {count}/{_npcsScanned} NPCs");
            }
        }
    }

    private void RegisterDistributedNpc(ResolvedRule rule, FormKey npc)
    {
        var key = (rule.Source.Kind, rule.DistributedForm);
        if (!_distributedNpcCounts.TryGetValue(key, out var npcs))
        {
            npcs = new HashSet<FormKey>();
            _distributedNpcCounts[key] = npcs;
        }

        npcs.Add(npc);
    }

    private static string FormatSpidForm(FormKey form)
    {
        return $"[0x{form.ID:X}~{form.ModKey.FileName.String}]";
    }

    private string ToSpidDataPath(string path)
    {
        string dataFolder = Path.GetFullPath(_state.DataFolderPath.ToString());
        string relative = Path.GetRelativePath(dataFolder, Path.GetFullPath(path))
            .Replace('/', '\\');
        return $"Data\\{relative}";
    }

    private void LogRegisteredRules(
        IReadOnlyCollection<SpidRule> parsedRules,
        IReadOnlyCollection<ResolvedRule> resolvedRules)
    {
        foreach (var kind in Enum.GetValues<DistributionKind>())
        {
            int all = parsedRules.Count(rule => rule.Kind == kind && KindEnabled(kind));
            if (all == 0) continue;

            int registered = resolvedRules.Count(rule => rule.Source.Kind == kind);
            SpidLog.Info($"Registered {registered}/{all} {GetSpidRecordName(kind)}s");
        }
    }

    private static string GetSpidRecordName(DistributionKind kind)
    {
        return kind switch
        {
            DistributionKind.Keyword => "Keyword",
            DistributionKind.Spell => "Spell",
            DistributionKind.Perk => "Perk",
            DistributionKind.Shout => "Shout",
            DistributionKind.Package => "Package",
            DistributionKind.Item => "Item",
            DistributionKind.Outfit => "Outfit",
            DistributionKind.SleepOutfit => "SleepOutfit",
            DistributionKind.Faction => "Faction",
            DistributionKind.Skin => "Skin",
            _ => kind.ToString()
        };
    }

    private IEnumerable<string> DiscoverIniFiles()
    {
        string dataFolder = _state.DataFolderPath.ToString();
        var candidates = new List<string>();
        candidates.AddRange(EnumerateDistrIniFiles(dataFolder));

        if (_settings.LookInConvertedFolder && Directory.Exists(_convertedFolderPath))
        {
            candidates.AddRange(EnumerateDistrIniFiles(_convertedFolderPath));
        }

        return candidates
            .Where(path => !_settings.OnlyReadListedIniFiles || _includedIniFiles.Contains(Path.GetFileName(path)))
            .Where(path => !_ignoredIniFiles.Contains(Path.GetFileName(path)))
            .GroupBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(path => IsInConvertedFolder(path) ? 1 : 0)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateDistrIniFiles(string folder)
    {
        if (!Directory.Exists(folder)) return Array.Empty<string>();

        return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".ini", StringComparison.OrdinalIgnoreCase))
            .Where(path => Path.GetFileName(path).EndsWith("_DISTR.ini", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsInConvertedFolder(string path)
    {
        string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
        return parent is not null &&
               string.Equals(
                   Path.TrimEndingDirectorySeparator(parent),
                   Path.TrimEndingDirectorySeparator(Path.GetFullPath(_convertedFolderPath)),
                   StringComparison.OrdinalIgnoreCase);
    }

    private void MoveProcessedIniFiles(IEnumerable<string> processedIniFiles)
    {
        if (!_settings.MoveProcessedIniFiles) return;

        Directory.CreateDirectory(_convertedFolderPath);
        foreach (var source in processedIniFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsInConvertedFolder(source))
            {
                continue;
            }

            string destination = Path.Combine(_convertedFolderPath, Path.GetFileName(source));
            try
            {
                File.Move(source, destination, overwrite: true);
            }
            catch (Exception ex)
            {
                Warn($"Could not move '{source}' to '{destination}': {ex.Message}");
            }
        }

    }

    private static HashSet<string> NormalizeIniFileNames(IEnumerable<string> names)
    {
        return names
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<ResolvedRule> ResolveRules(IEnumerable<SpidRule> rules, RecordIndex index)
    {
        int originalOrder = 0;
        foreach (var rule in rules)
        {
            int currentOrder = originalOrder++;
            if (!KindEnabled(rule.Kind)) continue;
            if (!TryResolveOrCreateDistributedForm(rule, index, out var distributedForm, out var failure))
            {
                LogDistributedFormFailure(rule, failure);
                continue;
            }

            var formFilters = new ResolvedFormFilterSet();
            ResolveFilterGroup(rule.FormFilters.Match, formFilters.Match, index, rule);
            ResolveFilterGroup(rule.FormFilters.All, formFilters.All, index, rule);
            ResolveFilterGroup(rule.FormFilters.Not, formFilters.Not, index, rule);

            yield return new ResolvedRule
            {
                Source = rule,
                DistributedForm = distributedForm,
                DistributedEditorId = index.GetEditorId(distributedForm),
                DistributedKind = index.GetKind(distributedForm),
                FormFilters = formFilters,
                OriginalOrder = currentOrder
            };
        }
    }

    private bool TryResolveOrCreateDistributedForm(
        SpidRule rule,
        RecordIndex index,
        out FormKey distributedForm,
        out FormResolutionFailure failure)
    {
        if (index.TryResolveDistributedForm(
                rule.Kind,
                rule.RawDistributedForm,
                out distributedForm,
                out failure))
        {
            return true;
        }

        if (rule.Kind != DistributionKind.Keyword ||
            !RecordIndex.IsBareEditorId(rule.RawDistributedForm))
        {
            return false;
        }

        var editorId = rule.RawDistributedForm.Trim();
        try
        {
            var keyword = _state.PatchMod.Keywords.AddNew(editorId);
            index.AddGeneratedKeyword(keyword);
            distributedForm = keyword.FormKey;
            failure = new FormResolutionFailure();
            _keywordsCreated++;
            return true;
        }
        catch
        {
            distributedForm = default;
            failure = new FormResolutionFailure
            {
                Kind = FormResolutionFailureKind.KeywordCreationFailed,
                Raw = editorId,
                ExpectedKind = IndexedFormKind.Keyword
            };
            return false;
        }
    }

    private void ResolveFilterGroup(
        IEnumerable<string> rawFilters,
        ICollection<ResolvedFormFilter> destination,
        RecordIndex index,
        SpidRule rule)
    {
        foreach (var raw in rawFilters)
        {
            var resolved = index.ResolveFilter(raw, out var failure);
            if (failure.Kind != FormResolutionFailureKind.None)
            {
                LogFilterFailure(rule, failure);
            }
            destination.Add(resolved);
        }
    }

    private void LogDistributedFormFailure(SpidRule rule, FormResolutionFailure failure)
    {
        string path = ToSpidRulePath(rule.SourcePath);
        string raw = failure.Raw.Trim();
        switch (failure.Kind)
        {
            case FormResolutionFailureKind.UnknownFormId when failure.HasFormKey:
                Warn($"\t[{path}] [0x{failure.FormKey.ID:X}] ({failure.FormKey.ModKey.FileName.String}) FAIL - formID doesn't exist");
                break;
            case FormResolutionFailureKind.UnknownFormId when failure.HasNumericFormId:
                Warn($"\t[{path}] [0x{failure.NumericFormId:X}] FAIL - formID doesn't exist");
                break;
            case FormResolutionFailureKind.InvalidKeyword:
                Warn($"\t[{path}] [0x{failure.FormKey.ID:X}] ({failure.FormKey.ModKey.FileName.String}) FAIL - keyword does not have a valid editorID");
                break;
            case FormResolutionFailureKind.KeywordCreationFailed:
                Warn($"\t[{path}] {raw} FAIL - couldn't create keyword");
                break;
            case FormResolutionFailureKind.UnknownEditorId:
                Warn($"\t[{path}] ({raw}) FAIL - editorID doesn't exist");
                break;
            case FormResolutionFailureKind.MalformedEditorId:
                Warn($"\t[{path}] FAIL - editorID can't be empty");
                break;
            case FormResolutionFailureKind.MismatchingFormType when failure.HasFormKey:
                Warn($"\t\t[{path}] [0x{failure.FormKey.ID:X}] ({failure.FormKey.ModKey.FileName.String}) FAIL - mismatching form type (expected: {GetSpidFormTypeName(failure.ExpectedKind)}, actual: {GetSpidFormTypeName(failure.ActualKind)})");
                break;
            case FormResolutionFailureKind.MismatchingFormType:
                Warn($"\t\t[{path}] ({raw}) FAIL - mismatching form type (expected: {GetSpidFormTypeName(failure.ExpectedKind)}, actual: {GetSpidFormTypeName(failure.ActualKind)})");
                break;
            case FormResolutionFailureKind.UnknownPlugin:
                Warn($"\t[{path}] ({failure.PluginName ?? raw}) FAIL - mod cannot be found");
                break;
        }
    }

    private void LogFilterFailure(SpidRule rule, FormResolutionFailure failure)
    {
        string path = ToSpidRulePath(rule.SourcePath);
        string raw = failure.Raw.Trim();
        switch (failure.Kind)
        {
            case FormResolutionFailureKind.UnknownFormId when failure.HasFormKey:
                Warn($"\t\t[{path}] Filter [0x{failure.FormKey.ID:X}] ({failure.FormKey.ModKey.FileName.String}) SKIP - formID doesn't exist");
                break;
            case FormResolutionFailureKind.UnknownFormId when failure.HasNumericFormId:
                Warn($"\t\t[{path}] Filter [0x{failure.NumericFormId:X}] SKIP - formID doesn't exist");
                break;
            case FormResolutionFailureKind.UnknownPlugin:
                Warn($"\t\t[{path}] Filter ({failure.PluginName ?? raw}) SKIP - mod cannot be found");
                break;
            case FormResolutionFailureKind.InvalidKeyword:
                Warn($"\t\t[{path}] Filter [0x{failure.FormKey.ID:X}] ({failure.FormKey.ModKey.FileName.String}) SKIP - keyword does not have a valid editorID");
                break;
            case FormResolutionFailureKind.UnknownEditorId:
                Warn($"\t\t[{path}] Filter ({raw}) SKIP - editorID doesn't exist");
                break;
            case FormResolutionFailureKind.MalformedEditorId:
                Warn($"\t\t[{path}] Filter (\"\") SKIP - malformed editorID");
                break;
            case FormResolutionFailureKind.MismatchingFormType when failure.HasFormKey:
                Warn($"\t\t[{path}] Filter[0x{failure.FormKey.ID:X}] ({failure.FormKey.ModKey.FileName.String}) FAIL - mismatching form type (expected: {GetSpidFormTypeName(failure.ExpectedKind)}, actual: {GetSpidFormTypeName(failure.ActualKind)})");
                break;
            case FormResolutionFailureKind.MismatchingFormType:
                Warn($"\t\t[{path}] Filter ({raw}) FAIL - mismatching form type (expected: {GetSpidFormTypeName(failure.ExpectedKind)}, actual: {GetSpidFormTypeName(failure.ActualKind)})");
                break;
        }
    }

    private string ToSpidRulePath(string path)
    {
        string dataFolder = Path.GetFullPath(_state.DataFolderPath.ToString());
        return Path.GetRelativePath(dataFolder, Path.GetFullPath(path)).Replace('/', '\\');
    }

    private static string GetSpidFormTypeName(IndexedFormKind kind)
    {
        return kind switch
        {
            IndexedFormKind.Npc => "NPC",
            IndexedFormKind.FormList => "FormList",
            IndexedFormKind.CombatStyle => "CombatStyle",
            IndexedFormKind.VoiceType => "VoiceType",
            _ => kind.ToString()
        };
    }

    private bool KindEnabled(DistributionKind kind)
    {
        return kind switch
        {
            DistributionKind.Keyword => _settings.EnableKeywords,
            DistributionKind.Spell => _settings.EnableSpells,
            DistributionKind.Perk => _settings.EnablePerks,
            DistributionKind.Shout => _settings.EnableShouts,
            DistributionKind.Package => _settings.EnablePackages,
            DistributionKind.Item => _settings.EnableItems,
            DistributionKind.Outfit => _settings.EnableOutfits,
            DistributionKind.SleepOutfit => _settings.EnableSleepOutfits,
            DistributionKind.Faction => _settings.EnableFactions,
            DistributionKind.Skin => _settings.EnableSkins,
            _ => false
        };
    }

    private static bool AlreadyHas(ResolvedRule rule, NpcEvaluationState npc)
    {
        return rule.Source.Kind switch
        {
            DistributionKind.Keyword => npc.Keywords.Contains(rule.DistributedForm),
            DistributionKind.Spell => npc.Spells.Contains(rule.DistributedForm),
            DistributionKind.Perk => npc.Perks.ContainsKey(rule.DistributedForm),
            DistributionKind.Shout => npc.Shouts.Contains(rule.DistributedForm),
            DistributionKind.Package when rule.DistributedKind == IndexedFormKind.Package =>
                npc.Packages.Contains(rule.DistributedForm),
            DistributionKind.Package when rule.DistributedKind == IndexedFormKind.FormList =>
                npc.GetPackageList(rule.Source.PackageIndex) == rule.DistributedForm,
            DistributionKind.Faction => npc.Factions.Contains(rule.DistributedForm),
            DistributionKind.Item => false,
            DistributionKind.Outfit => npc.DefaultOutfit == rule.DistributedForm,
            DistributionKind.SleepOutfit => npc.SleepingOutfit == rule.DistributedForm,
            DistributionKind.Skin => npc.Skin == rule.DistributedForm,
            _ => true
        };
    }

    private bool Apply(ResolvedRule rule, NpcEvaluationState npc)
    {
        return rule.Source.Kind switch
        {
            DistributionKind.Keyword => AddKeyword(rule.DistributedForm, npc),
            DistributionKind.Spell => AddSpell(rule, npc),
            DistributionKind.Perk => AddPerk(rule.DistributedForm, npc),
            DistributionKind.Shout => AddShout(rule.DistributedForm, npc),
            DistributionKind.Package => AddPackage(rule, npc),
            DistributionKind.Faction => AddFaction(rule.DistributedForm, npc),
            DistributionKind.Item => AddItem(rule, npc),
            DistributionKind.Outfit => AddOutfit(rule.DistributedForm, npc),
            DistributionKind.SleepOutfit => AddSleepOutfit(rule.DistributedForm, npc),
            DistributionKind.Skin => AddSkin(rule.DistributedForm, npc),
            _ => false
        };
    }

    private bool AddKeyword(FormKey keyword, NpcEvaluationState npc)
    {
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        modified.Keywords ??= new();
        if (modified.Keywords.Any(x => x.FormKey == keyword)) return false;
        modified.Keywords.Add(keyword);
        npc.RegisterKeyword(keyword);
        return true;
    }

    private bool AddSpell(ResolvedRule rule, NpcEvaluationState npc)
    {
        if (!UsesLeveledChance(rule.Source))
        {
            return AddDirectSpell(rule.DistributedForm, npc);
        }

        FormKey leveledSpell = GetOrCreateChanceLeveledSpell(rule);
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        modified.ActorEffect ??= new();
        if (modified.ActorEffect.Any(x => x.FormKey == leveledSpell)) return false;

        modified.ActorEffect.Add(new FormLink<ISpellRecordGetter>(leveledSpell));

        // Track the record actually placed on the NPC. The original spell remains
        // conditional at runtime and must not be treated as guaranteed by later rules.
        npc.Spells.Add(leveledSpell);
        npc.RelatedForms.Add(leveledSpell);
        return true;
    }

    private bool AddDirectSpell(FormKey spell, NpcEvaluationState npc)
    {
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        modified.ActorEffect ??= new();
        if (modified.ActorEffect.Any(x => x.FormKey == spell)) return false;
        modified.ActorEffect.Add(new FormLink<ISpellGetter>(spell));
        npc.Spells.Add(spell);
        npc.RelatedForms.Add(spell);
        return true;
    }

    private bool AddPerk(FormKey perk, NpcEvaluationState npc)
    {
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        modified.Perks ??= new();
        if (modified.Perks.Any(x => x.Perk.FormKey == perk)) return false;
        modified.Perks.Add(new PerkPlacement
        {
            Perk = new FormLink<IPerkGetter>(perk),
            Rank = 1
        });
        npc.Perks[perk] = 1;
        npc.RelatedForms.Add(perk);
        return true;
    }

    private bool AddShout(FormKey shout, NpcEvaluationState npc)
    {
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        modified.ActorEffect ??= new();
        if (modified.ActorEffect.Any(x => x.FormKey == shout)) return false;
        modified.ActorEffect.Add(new FormLink<ISpellRecordGetter>(shout));
        npc.RegisterShout(shout);
        return true;
    }

    private bool AddFaction(FormKey faction, NpcEvaluationState npc)
    {
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        if (modified.Factions.Any(x => x.Faction.FormKey == faction)) return false;
        modified.Factions.Add(new RankPlacement
        {
            Faction = new FormLink<IFactionGetter>(faction),
            Rank = 1
        });
        npc.RegisterFaction(faction);
        return true;
    }

    private bool AddPackage(ResolvedRule rule, NpcEvaluationState npc)
    {
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);

        if (rule.DistributedKind == IndexedFormKind.Package)
        {
            if (modified.Packages.Any(x => x.FormKey == rule.DistributedForm)) return false;
            int index = Math.Clamp(rule.Source.PackageIndex, 0, modified.Packages.Count);
            modified.Packages.Insert(index, new FormLink<IPackageGetter>(rule.DistributedForm));
            npc.RegisterPackage(rule.DistributedForm);
            return true;
        }

        if (rule.DistributedKind != IndexedFormKind.FormList) return false;
        int slot = rule.Source.PackageIndex;
        if (slot is < 0 or > 4)
        {
            return false;
        }

        switch (slot)
        {
            case 0: modified.DefaultPackageList.SetTo(rule.DistributedForm); break;
            case 1: modified.SpectatorOverridePackageList.SetTo(rule.DistributedForm); break;
            case 2: modified.ObserveDeadBodyOverridePackageList.SetTo(rule.DistributedForm); break;
            case 3: modified.GuardWarnOverridePackageList.SetTo(rule.DistributedForm); break;
            case 4: modified.CombatOverridePackageList.SetTo(rule.DistributedForm); break;
        }

        npc.RegisterPackageList(slot, rule.DistributedForm);
        return true;
    }

    private bool AddItem(ResolvedRule rule, NpcEvaluationState npc)
    {
        int count = RuleMatcher.SelectItemCount(
            rule.Source,
            npc.Source.FormKey.ToString(),
            _runSeed);
        if (count <= 0) return false;

        if (UsesLeveledChance(rule.Source))
        {
            return AddChanceItem(rule, npc, count);
        }

        return AddDirectItem(rule.DistributedForm, npc, count);
    }

    private bool AddChanceItem(ResolvedRule rule, NpcEvaluationState npc, int count)
    {
        FormKey leveledItem = GetOrCreateChanceLeveledItem(rule, count);
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        modified.Items ??= new();
        if (modified.Items.Any(x => x.Item.Item.FormKey == leveledItem)) return false;

        modified.Items.Add(new ContainerEntry
        {
            Item = new ContainerItem
            {
                Item = new FormLink<IItemGetter>(leveledItem),
                Count = 1
            }
        });

        // Track the record actually placed in the inventory. The original item remains
        // conditional at runtime and must not be treated as guaranteed by later rules.
        npc.RegisterItem(leveledItem, 1);
        return true;
    }

    private bool AddDirectItem(FormKey item, NpcEvaluationState npc, int count)
    {
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        modified.Items ??= new();
        var existing = modified.Items.FirstOrDefault(x => x.Item.Item.FormKey == item);
        if (existing is not null)
        {
            existing.Item.Count += count;
        }
        else
        {
            modified.Items.Add(new ContainerEntry
            {
                Item = new ContainerItem
                {
                    Item = new FormLink<IItemGetter>(item),
                    Count = count
                }
            });
        }

        npc.RegisterItem(item, count);
        return true;
    }

    private FormKey GetOrCreateChanceLeveledItem(ResolvedRule rule, int count)
    {
        var cacheKey = (rule.Source, count);
        if (_chanceItemLists.TryGetValue(cacheKey, out var existing)) return existing;

        int leveledCount = Math.Clamp(count, 1, short.MaxValue);
        if (leveledCount != count)
        {
            Warn($"[{ToSpidDataPath(rule.Source.SourcePath)}:{rule.Source.LineNumber}] " +
                 $"item count {count} exceeds the LVLI limit and was clamped to {leveledCount}");
        }

        string editorId = BuildChanceEditorId(rule, "Item", ++_chanceRecordsCreated);
        var leveledItem = _state.PatchMod.LeveledItems.AddNew(editorId);
        leveledItem.ChanceNone = CreatePercent(100.0 - rule.Source.Chance.Percent);
        leveledItem.Entries = new();
        leveledItem.Entries.Add(new LeveledItemEntry
        {
            Data = new LeveledItemEntryData
            {
                Level = 1,
                Reference = new FormLink<IItemGetter>(rule.DistributedForm),
                Count = (short)leveledCount
            }
        });

        _chanceItemLists[cacheKey] = leveledItem.FormKey;
        _leveledItemsCreated++;
        return leveledItem.FormKey;
    }

    private FormKey GetOrCreateChanceLeveledSpell(ResolvedRule rule)
    {
        if (_chanceSpellLists.TryGetValue(rule.Source, out var existing)) return existing;

        string editorId = BuildChanceEditorId(rule, "Spell", ++_chanceRecordsCreated);
        var leveledSpell = _state.PatchMod.LeveledSpells.AddNew(editorId);
        leveledSpell.ChanceNone = CreatePercent(100.0 - rule.Source.Chance.Percent);
        leveledSpell.Entries = new();
        leveledSpell.Entries.Add(new LeveledSpellEntry
        {
            Data = new LeveledSpellEntryData
            {
                Level = 1,
                Reference = new FormLink<ISpellRecordGetter>(rule.DistributedForm),
                Count = 1
            }
        });

        _chanceSpellLists[rule.Source] = leveledSpell.FormKey;
        _leveledSpellsCreated++;
        return leveledSpell.FormKey;
    }

    private static bool UsesLeveledChance(SpidRule rule)
    {
        return rule.HasChanceCondition &&
               rule.Kind is DistributionKind.Item or DistributionKind.Spell;
    }

    private static string BuildChanceEditorId(ResolvedRule rule, string recordType, int sequence)
    {
        string originalEditorId = string.IsNullOrWhiteSpace(rule.DistributedEditorId)
            ? $"Form{rule.DistributedForm.ID:X6}_{rule.DistributedForm.ModKey.Name}"
            : rule.DistributedEditorId;

        return $"{originalEditorId}_SpithesisChance{recordType}{sequence}";
    }

    private static Percent CreatePercent(double percentage)
    {
        double ratio = Math.Clamp(percentage, 0.0, 100.0) / 100.0;
        return new Percent(ratio);
    }

    private bool AddOutfit(FormKey outfit, NpcEvaluationState npc)
    {
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        if (modified.DefaultOutfit.FormKey == outfit) return false;

        modified.DefaultOutfit.SetTo(outfit);
        npc.RegisterOutfit(outfit);
        return true;
    }

    private bool AddSleepOutfit(FormKey outfit, NpcEvaluationState npc)
    {
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        if (modified.SleepingOutfit.FormKey == outfit) return false;

        modified.SleepingOutfit.SetTo(outfit);
        npc.RegisterSleepOutfit(outfit);
        return true;
    }

    private bool AddSkin(FormKey armor, NpcEvaluationState npc)
    {
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        if (modified.WornArmor.FormKey == armor) return false;

        modified.WornArmor.SetTo(armor);
        npc.RegisterSkin(armor);
        return true;
    }

    private void Warn(string message)
    {
        SpidLog.Warn(message);
    }

}
