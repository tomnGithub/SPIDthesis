using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace SPIDThesis;

internal sealed class SPIDThesisDistributor
{
    private static readonly FormKey PlayerFormKey = FormKey.Factory("000007:Skyrim.esm");

    private readonly IPatcherState<ISkyrimMod, ISkyrimModGetter> _state;
    private readonly Settings _settings;
    private readonly HashSet<string> _ignoredIniFiles;
    private readonly HashSet<string> _ignoredNpcPlugins;
    private readonly HashSet<string> _warningOnce = new(StringComparer.OrdinalIgnoreCase);

    private int _keywordsCreated;
    private int _keywordsAdded;
    private int _spellsAdded;
    private int _perksAdded;
    private int _npcsChanged;
    private int _rulesSkipped;

    public SPIDThesisDistributor(
        IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
        Settings settings)
    {
        _state = state;
        _settings = settings;
        _ignoredIniFiles = settings.IgnoredIniFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ignoredNpcPlugins = settings.IgnoredNpcPlugins.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void Run()
    {
        var iniFiles = DiscoverIniFiles().ToArray();
        Console.WriteLine($"SPIDThesis: found {iniFiles.Length} *_DISTR.ini file(s) in {_state.DataFolderPath}.");
        if (iniFiles.Length == 0) return;

        var parsedRules = SpidIniParser.ParseFiles(iniFiles, Warn);
        Console.WriteLine($"SPIDThesis: parsed {parsedRules.Count} Keyword/Spell/Perk rule(s).");

        var index = RecordIndex.Build(_state);
        var resolvedRules = ResolveRules(parsedRules, index).ToArray();
        resolvedRules = KeywordRuleOrdering.OrderLikeSpid(resolvedRules, index, Warn).ToArray();
        Console.WriteLine($"SPIDThesis: resolved {resolvedRules.Length} rule(s); {_rulesSkipped} rule(s) skipped.");

        if (_settings.VerboseLogging || resolvedRules.Length <= 25)
        {
            Console.WriteLine("SPIDThesis: resolved form filters:");
            foreach (var rule in resolvedRules)
            {
                LogResolvedFilters(rule.Source, rule.FormFilters, index);
            }
        }

        var ruleStats = resolvedRules.ToDictionary(rule => rule, _ => new RuleRunStats());
        int npcRecordsScanned = 0;

        foreach (var npc in _state.LoadOrder.PriorityOrder.Npc().WinningOverrides())
        {
            if (!_settings.PatchPlayer && npc.FormKey == PlayerFormKey) continue;
            if (_ignoredNpcPlugins.Contains(npc.FormKey.ModKey.FileName.String)) continue;

            npcRecordsScanned++;
            var evaluation = NpcEvaluationState.Create(
                _state,
                index,
                npc,
                _settings.EnableTemplateHandling);
            bool changed = false;

            foreach (var rule in resolvedRules)
            {
                if (!KindEnabled(rule.Source.Kind)) continue;
                if (!RuleMatcher.Matches(rule, evaluation, _settings)) continue;

                var stats = ruleStats[rule];
                stats.Matched++;

                if (AlreadyHas(rule, evaluation))
                {
                    stats.AlreadyHad++;
                    continue;
                }

                if (Apply(rule, evaluation))
                {
                    stats.Applied++;
                    changed = true;
                    if (_settings.VerboseLogging)
                    {
                        Console.WriteLine($"  {npc.EditorID ?? npc.FormKey.ToString()}: +{rule.Source.Kind} {rule.DistributedForm} ({Path.GetFileName(rule.Source.SourcePath)}:{rule.Source.LineNumber})");
                    }
                }
            }

            if (changed) _npcsChanged++;
        }

        string matchingMode = _settings.EnableTemplateHandling
            ? "template-aware mode"
            : "direct-record mode (template handling disabled)";
        Console.WriteLine($"SPIDThesis: scanned {npcRecordsScanned} winning NPC record(s) in {matchingMode}.");
        int rulesWithoutMatches = 0;
        bool printRuleDetails = _settings.VerboseLogging || resolvedRules.Length <= 25;

        foreach (var rule in resolvedRules)
        {
            var stats = ruleStats[rule];
            if (stats.Matched == 0) rulesWithoutMatches++;
            if (!printRuleDetails) continue;

            string source = $"{Path.GetFileName(rule.Source.SourcePath)}:{rule.Source.LineNumber}";
            Console.WriteLine(
                $"  Rule {source} {rule.Source.Kind} '{rule.Source.RawDistributedForm}': " +
                $"matched {stats.Matched}, applied {stats.Applied}, already present {stats.AlreadyHad}.");
        }

        if (!printRuleDetails && rulesWithoutMatches > 0)
        {
            Console.WriteLine(
                $"SPIDThesis: {rulesWithoutMatches} rule(s) matched no NPCs. " +
                "Enable verbose logging for per-rule counts.");
        }

        Console.WriteLine("SPIDThesis complete.");
        Console.WriteLine($"  NPCs changed: {_npcsChanged}");
        Console.WriteLine($"  Keyword records created: {_keywordsCreated}");
        Console.WriteLine($"  Keywords added: {_keywordsAdded}");
        Console.WriteLine($"  Spells added: {_spellsAdded}");
        Console.WriteLine($"  Perks added: {_perksAdded}");
    }

    private IEnumerable<string> DiscoverIniFiles()
    {
        var option = _settings.SearchSubdirectories
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        return Directory.EnumerateFiles(_state.DataFolderPath.ToString(), "*.ini", option)
            .Where(path => Path.GetFileName(path).EndsWith("_DISTR.ini", StringComparison.OrdinalIgnoreCase))
            .Where(path => !_ignoredIniFiles.Contains(Path.GetFileName(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<ResolvedRule> ResolveRules(IEnumerable<SpidRule> rules, RecordIndex index)
    {
        int originalOrder = 0;
        foreach (var rule in rules)
        {
            int currentOrder = originalOrder++;
            if (!KindEnabled(rule.Kind)) continue;
            if (rule.HadUnsupportedFinalPrefix)
            {
                WarnOnce($"final:{rule.SourcePath}:{rule.LineNumber}",
                    $"{Path.GetFileName(rule.SourcePath)}:{rule.LineNumber}: SPID's Final modifier only applies to Outfit rules; it is ignored for {rule.Kind}.");
            }

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
                FormFilters = formFilters,
                OriginalOrder = currentOrder
            };
        }
    }


    private static void LogResolvedFilters(
        SpidRule rule,
        ResolvedFormFilterSet filters,
        RecordIndex index)
    {
        foreach (var (groupName, group) in new[]
                 {
                     ("match", filters.Match),
                     ("all", filters.All),
                     ("not", filters.Not)
                 })
        {
            foreach (var filter in group)
            {
                if (filter.IsPlugin)
                {
                    Console.WriteLine(
                        $"  Filter {Path.GetFileName(rule.SourcePath)}:{rule.LineNumber} {groupName} '{filter.Raw}' -> plugin '{filter.PluginName}'.");
                    continue;
                }

                if (filter.Candidates.Count == 0)
                {
                    Console.WriteLine(
                        $"  Filter {Path.GetFileName(rule.SourcePath)}:{rule.LineNumber} {groupName} '{filter.Raw}' -> unresolved.");
                    continue;
                }

                string resolved = string.Join(", ", filter.Candidates.Select(candidate =>
                {
                    string editorId = candidate.EditorId ?? index.GetEditorId(candidate.FormKey) ?? "<no EDID>";
                    return $"{editorId} [{candidate.Kind}] ({candidate.FormKey})";
                }));

                Console.WriteLine(
                    $"  Filter {Path.GetFileName(rule.SourcePath)}:{rule.LineNumber} {groupName} '{filter.Raw}' -> {resolved}.");
            }
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

        Console.WriteLine(
            $"  Created keyword {editorId} ({keyword.FormKey}) for {Path.GetFileName(rule.SourcePath)}:{rule.LineNumber}.");
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
            _ => false
        };
    }

    private bool AddKeyword(FormKey keyword, NpcEvaluationState npc)
    {
        var modified = _state.PatchMod.Npcs.GetOrAddAsOverride(npc.Source);
        if (_settings.EnableTemplateHandling && npc.InheritsKeywords && !npc.KeywordsMaterialized)
        {
            if (!_settings.MaterializeInheritedLists || !npc.CanMaterializeKeywords)
            {
                return false;
            }

            modified.Keywords = new();
            foreach (var inherited in npc.Keywords)
            {
                modified.Keywords.Add(inherited);
            }
            modified.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.Keywords;
            npc.KeywordsMaterialized = true;
        }

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
        if (!EnsureSpellListMaterialized(modified, npc)) return false;

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
        if (!EnsureSpellListMaterialized(modified, npc)) return false;

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

    private bool EnsureSpellListMaterialized(Npc modified, NpcEvaluationState npc)
    {
        if (!_settings.EnableTemplateHandling || !npc.InheritsSpellList || npc.SpellListMaterialized) return true;
        if (!_settings.MaterializeInheritedLists || !npc.CanMaterializeSpellList)
        {
            return false;
        }

        modified.ActorEffect = new();
        foreach (var inheritedSpell in npc.Spells)
        {
            modified.ActorEffect.Add(new FormLink<ISpellGetter>(inheritedSpell));
        }

        modified.Perks = new();
        foreach (var inheritedPerk in npc.Perks)
        {
            modified.Perks.Add(new PerkPlacement
            {
                Perk = new FormLink<IPerkGetter>(inheritedPerk.Key),
                Rank = inheritedPerk.Value
            });
        }

        modified.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.SpellList;
        npc.SpellListMaterialized = true;
        return true;
    }

    private void Warn(string message)
    {
        Console.WriteLine($"WARNING: {message}");
    }

    private void WarnOnce(string key, string message)
    {
        if (_warningOnce.Add(key)) Warn(message);
    }

    private sealed class RuleRunStats
    {
        public int Matched { get; set; }
        public int Applied { get; set; }
        public int AlreadyHad { get; set; }
    }
}
