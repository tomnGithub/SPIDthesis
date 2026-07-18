using System.Diagnostics;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

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
    private readonly HashSet<string> _warningOnce = new(StringComparer.OrdinalIgnoreCase);

    private int _keywordsCreated;
    private int _keywordsAdded;
    private int _spellsAdded;
    private int _perksAdded;
    private int _shoutsAdded;
    private int _packagesAdded;
    private int _packageListsAssigned;
    private int _factionsAdded;
    private int _sleepOutfitsAssigned;
    private int _skinsAssigned;
    private int _itemDistributionsApplied;
    private long _itemQuantityAdded;
    private int _outfitsAssigned;
    private int _npcsScanned;
    private int _npcsChanged;
    private int _rulesSkipped;

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

        SpidLog.Header("LOOKUP");
        var lookupTimer = Stopwatch.StartNew();

        var index = RecordIndex.Build(_state);
        var resolvedRules = ResolveRules(parsedRules, index).ToArray();
        resolvedRules = KeywordRuleOrdering.OrderLikeSpid(resolvedRules, index, Warn).ToArray();

        lookupTimer.Stop();
        if (resolvedRules.Length > 0)
        {
            SpidLog.Header("PROCESSING");
            LogRegisteredRules(parsedRules, resolvedRules);
            long lookupMicroseconds = lookupTimer.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
            SpidLog.Info($"Lookup took {lookupMicroseconds}μs / {lookupTimer.ElapsedMilliseconds}ms");

            var distributionTimer = Stopwatch.StartNew();
            foreach (var npc in _state.LoadOrder.PriorityOrder.Npc().WinningOverrides())
            {
                if (npc.FormKey == PlayerFormKey) continue;
                _npcsScanned++;

                var evaluation = NpcEvaluationState.Create(index, npc);
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
                    if (!RuleMatcher.Matches(rule, evaluation, _runSeed)) continue;

                    if (rule.Source.Kind == DistributionKind.Outfit) outfitRuleSettled = true;
                    if (rule.Source.Kind == DistributionKind.SleepOutfit) sleepOutfitRuleSettled = true;
                    if (rule.Source.Kind == DistributionKind.Skin) skinRuleSettled = true;

                    if (AlreadyHas(rule, evaluation)) continue;
                    if (Apply(rule, evaluation)) changed = true;
                }

                if (changed) _npcsChanged++;
            }

            distributionTimer.Stop();
            LogDistributionSummary(parsedRules, distributionTimer);
        }

        MoveProcessedIniFiles(parseResult.ProcessedFiles);
    }

    private void LogDistributionSummary(
        IReadOnlyCollection<SpidRule> parsedRules,
        Stopwatch distributionTimer)
    {
        SpidLog.Header("DISTRIBUTION");
        SpidLog.Info($"Patched {_npcsChanged}/{_npcsScanned} NPCs");

        if (_keywordsCreated > 0)
        {
            SpidLog.Info($"Created {_keywordsCreated} Keywords");
        }

        LogAppliedCount(parsedRules, DistributionKind.Keyword, _keywordsAdded, "Keywords");
        LogAppliedCount(parsedRules, DistributionKind.Spell, _spellsAdded, "Spells");
        LogAppliedCount(parsedRules, DistributionKind.Perk, _perksAdded, "Perks");
        LogAppliedCount(parsedRules, DistributionKind.Shout, _shoutsAdded, "Shouts");

        if (parsedRules.Any(rule => rule.Kind == DistributionKind.Package && KindEnabled(rule.Kind)))
        {
            SpidLog.Info($"Distributed {_packagesAdded} Packages");
            SpidLog.Info($"Assigned {_packageListsAssigned} Package Lists");
        }

        if (parsedRules.Any(rule => rule.Kind == DistributionKind.Item && KindEnabled(rule.Kind)))
        {
            SpidLog.Info($"Distributed {_itemDistributionsApplied} Item entries / {_itemQuantityAdded} total Items");
        }

        LogAppliedCount(parsedRules, DistributionKind.Outfit, _outfitsAssigned, "Outfits", "Assigned");
        LogAppliedCount(parsedRules, DistributionKind.SleepOutfit, _sleepOutfitsAssigned, "SleepOutfits", "Assigned");
        LogAppliedCount(parsedRules, DistributionKind.Faction, _factionsAdded, "Factions");
        LogAppliedCount(parsedRules, DistributionKind.Skin, _skinsAssigned, "Skins", "Assigned");

        if (_rulesSkipped > 0)
        {
            SpidLog.Info($"Skipped {_rulesSkipped} unresolved rules");
        }

        long distributionMicroseconds = distributionTimer.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
        SpidLog.Info($"Distribution took {distributionMicroseconds}μs / {distributionTimer.ElapsedMilliseconds}ms");
    }

    private void LogAppliedCount(
        IReadOnlyCollection<SpidRule> parsedRules,
        DistributionKind kind,
        long count,
        string recordName,
        string verb = "Distributed")
    {
        if (parsedRules.Any(rule => rule.Kind == kind && KindEnabled(kind)))
        {
            SpidLog.Info($"{verb} {count} {recordName}");
        }
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
            if (!TryResolveOrCreateDistributedForm(rule, index, out var distributedForm))
            {
                Warn($"{Path.GetFileName(rule.SourcePath)}:{rule.LineNumber}: could not resolve {rule.Kind} '{rule.RawDistributedForm}'. Rule skipped.");
                _rulesSkipped++;
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
                DistributedKind = index.GetKind(distributedForm),
                FormFilters = formFilters,
                OriginalOrder = currentOrder
            };
        }
    }

    private bool TryResolveOrCreateDistributedForm(
        SpidRule rule,
        RecordIndex index,
        out FormKey distributedForm)
    {
        if (index.TryResolveDistributedForm(rule.Kind, rule.RawDistributedForm, out distributedForm))
        {
            return true;
        }

        if (rule.Kind != DistributionKind.Keyword ||
            !RecordIndex.IsBareEditorId(rule.RawDistributedForm))
        {
            distributedForm = default;
            return false;
        }

        var editorId = rule.RawDistributedForm.Trim();
        var keyword = _state.PatchMod.Keywords.AddNew(editorId);
        index.AddGeneratedKeyword(keyword);
        distributedForm = keyword.FormKey;
        _keywordsCreated++;

        return true;
    }

    private void ResolveFilterGroup(
        IEnumerable<string> rawFilters,
        ICollection<ResolvedFormFilter> destination,
        RecordIndex index,
        SpidRule rule)
    {
        foreach (var raw in rawFilters)
        {
            var resolved = index.ResolveFilter(raw);
            if (!resolved.IsResolved)
            {
                WarnOnce($"filter:{raw}",
                    $"Unresolved SPID form filter '{raw}' (first seen in {Path.GetFileName(rule.SourcePath)}:{rule.LineNumber}). Positive use fails closed; negative use has no effect.");
            }
            destination.Add(resolved);
        }
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
            DistributionKind.Spell => AddSpell(rule.DistributedForm, npc),
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
        _keywordsAdded++;
        return true;
    }

    private bool AddSpell(FormKey spell, NpcEvaluationState npc)
    {
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        modified.ActorEffect ??= new();
        if (modified.ActorEffect.Any(x => x.FormKey == spell)) return false;
        modified.ActorEffect.Add(new FormLink<ISpellGetter>(spell));
        npc.Spells.Add(spell);
        npc.RelatedForms.Add(spell);
        _spellsAdded++;
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
        _perksAdded++;
        return true;
    }

    private bool AddShout(FormKey shout, NpcEvaluationState npc)
    {
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        modified.ActorEffect ??= new();
        if (modified.ActorEffect.Any(x => x.FormKey == shout)) return false;
        modified.ActorEffect.Add(new FormLink<ISpellRecordGetter>(shout));
        npc.RegisterShout(shout);
        _shoutsAdded++;
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
        _factionsAdded++;
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
            _packagesAdded++;
            return true;
        }

        if (rule.DistributedKind != IndexedFormKind.FormList) return false;
        int slot = rule.Source.PackageIndex;
        if (slot is < 0 or > 4)
        {
            WarnOnce(
                $"package-list-index:{rule.Source.SourcePath}:{rule.Source.LineNumber}",
                $"{Path.GetFileName(rule.Source.SourcePath)}:{rule.Source.LineNumber}: package FormList index {slot} is outside SPID's supported 0-4 range. Rule skipped.");
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
        _packageListsAssigned++;
        return true;
    }

    private bool AddItem(ResolvedRule rule, NpcEvaluationState npc)
    {
        int count = RuleMatcher.SelectItemCount(
            rule.Source,
            npc.Source.FormKey.ToString(),
            _runSeed);
        if (count <= 0) return false;

        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        modified.Items ??= new();
        var existing = modified.Items.FirstOrDefault(x => x.Item.Item.FormKey == rule.DistributedForm);
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
                    Item = new FormLink<IItemGetter>(rule.DistributedForm),
                    Count = count
                }
            });
        }

        npc.RegisterItem(rule.DistributedForm, count);
        _itemDistributionsApplied++;
        _itemQuantityAdded += count;
        return true;
    }

    private bool AddOutfit(FormKey outfit, NpcEvaluationState npc)
    {
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        if (modified.DefaultOutfit.FormKey == outfit) return false;

        modified.DefaultOutfit.SetTo(outfit);
        npc.RegisterOutfit(outfit);
        _outfitsAssigned++;
        return true;
    }

    private bool AddSleepOutfit(FormKey outfit, NpcEvaluationState npc)
    {
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        if (modified.SleepingOutfit.FormKey == outfit) return false;

        modified.SleepingOutfit.SetTo(outfit);
        npc.RegisterSleepOutfit(outfit);
        _sleepOutfitsAssigned++;
        return true;
    }

    private bool AddSkin(FormKey armor, NpcEvaluationState npc)
    {
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        if (modified.WornArmor.FormKey == armor) return false;

        modified.WornArmor.SetTo(armor);
        npc.RegisterSkin(armor);
        _skinsAssigned++;
        return true;
    }

    private void Warn(string message)
    {
        SpidLog.Warn(message);
    }

    private void WarnOnce(string key, string message)
    {
        if (_warningOnce.Add(key)) Warn(message);
    }

}
