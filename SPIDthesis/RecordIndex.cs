using System.Globalization;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace SPIDthesis;

internal sealed class RecordIndex
{
    private readonly Dictionary<string, List<IndexedForm>> _byEditorId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<FormKey, IndexedForm> _byFormKey = new();
    private readonly Dictionary<uint, List<IndexedForm>> _byBareFormId = new();
    private readonly Dictionary<ModKey, RuntimeFormIdMapping> _runtimeFormIdsByMod = new();
    private readonly HashSet<string> _knownPlugins = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<FormKey, IKeywordGetter> Keywords { get; } = new();
    public Dictionary<FormKey, ISpellGetter> Spells { get; } = new();
    public Dictionary<FormKey, IPerkGetter> Perks { get; } = new();
    public Dictionary<FormKey, IShoutGetter> Shouts { get; } = new();
    public Dictionary<FormKey, IPackageGetter> Packages { get; } = new();
    public Dictionary<FormKey, IFactionGetter> Factions { get; } = new();
    public Dictionary<FormKey, IArmorGetter> Armors { get; } = new();
    public Dictionary<FormKey, IItemGetter> Items { get; } = new();
    public Dictionary<FormKey, IOutfitGetter> Outfits { get; } = new();
    public Dictionary<FormKey, IRaceGetter> Races { get; } = new();
    public Dictionary<FormKey, HashSet<FormKey>> FormListItems { get; } = new();

    private RecordIndex(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
    {
        uint fullIndex = 0;
        uint lightIndex = 0;

        foreach (var listing in state.LoadOrder.ListedOrder)
        {
            if (!listing.Enabled || listing.Mod is null) continue;

            if (listing.Mod.IsSmallMaster)
            {
                if (lightIndex <= 0xFFF)
                {
                    _runtimeFormIdsByMod[listing.ModKey] = new RuntimeFormIdMapping(
                        Prefix: 0xFE000000u | (lightIndex << 12),
                        LocalIdMask: 0xFFFu);
                }
                lightIndex++;
            }
            else
            {
                if (fullIndex <= 0xFD)
                {
                    _runtimeFormIdsByMod[listing.ModKey] = new RuntimeFormIdMapping(
                        Prefix: fullIndex << 24,
                        LocalIdMask: 0xFFFFFFu);
                }
                fullIndex++;
            }
        }
    }

    public static RecordIndex Build(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
    {
        var index = new RecordIndex(state);

        foreach (var record in state.LoadOrder.PriorityOrder.Keyword().WinningOverrides())
        {
            index.Add(record.FormKey, record.EditorID, IndexedFormKind.Keyword);
            index.Keywords[record.FormKey] = record;
        }

        foreach (var record in state.LoadOrder.PriorityOrder.Spell().WinningOverrides())
        {
            index.Add(record.FormKey, record.EditorID, IndexedFormKind.Spell);
            index.Spells[record.FormKey] = record;
        }

        foreach (var record in state.LoadOrder.PriorityOrder.Perk().WinningOverrides())
        {
            index.Add(record.FormKey, record.EditorID, IndexedFormKind.Perk);
            index.Perks[record.FormKey] = record;
        }

        foreach (var record in state.LoadOrder.PriorityOrder.Shout().WinningOverrides())
        {
            index.Add(record.FormKey, record.EditorID, IndexedFormKind.Shout);
            index.Shouts[record.FormKey] = record;
        }

        foreach (var record in state.LoadOrder.PriorityOrder.Package().WinningOverrides())
        {
            index.Add(record.FormKey, record.EditorID, IndexedFormKind.Package);
            index.Packages[record.FormKey] = record;
        }

        foreach (var record in state.LoadOrder.PriorityOrder.Armor().WinningOverrides())
        {
            index.Add(record.FormKey, record.EditorID, IndexedFormKind.Armor);
            index.Armors[record.FormKey] = record;
        }

        foreach (var record in state.LoadOrder.PriorityOrder.IItem().WinningOverrides())
        {
            index.Add(record.FormKey, record.EditorID, IndexedFormKind.Item);
            index.Items[record.FormKey] = record;
        }

        foreach (var record in state.LoadOrder.PriorityOrder.Race().WinningOverrides())
        {
            index.Add(record.FormKey, record.EditorID, IndexedFormKind.Race);
            index.Races[record.FormKey] = record;
        }

        foreach (var record in state.LoadOrder.PriorityOrder.Npc().WinningOverrides())
        {
            index.Add(record.FormKey, record.EditorID, IndexedFormKind.Npc);
        }

        foreach (var record in state.LoadOrder.PriorityOrder.Faction().WinningOverrides())
        {
            index.Add(record.FormKey, record.EditorID, IndexedFormKind.Faction);
            index.Factions[record.FormKey] = record;
        }

        foreach (var record in state.LoadOrder.PriorityOrder.Class().WinningOverrides())
        {
            index.Add(record.FormKey, record.EditorID, IndexedFormKind.Class);
        }

        foreach (var record in state.LoadOrder.PriorityOrder.CombatStyle().WinningOverrides())
        {
            index.Add(record.FormKey, record.EditorID, IndexedFormKind.CombatStyle);
        }

        foreach (var record in state.LoadOrder.PriorityOrder.VoiceType().WinningOverrides())
        {
            index.Add(record.FormKey, record.EditorID, IndexedFormKind.VoiceType);
        }

        foreach (var record in state.LoadOrder.PriorityOrder.Outfit().WinningOverrides())
        {
            index.Add(record.FormKey, record.EditorID, IndexedFormKind.Outfit);
            index.Outfits[record.FormKey] = record;
        }

        foreach (var record in state.LoadOrder.PriorityOrder.FormList().WinningOverrides())
        {
            index.Add(record.FormKey, record.EditorID, IndexedFormKind.FormList);
            index.FormListItems[record.FormKey] = record.Items.Select(x => x.FormKey).ToHashSet();
        }

        return index;
    }

    public void AddGeneratedKeyword(IKeywordGetter keyword)
    {
        Add(keyword.FormKey, keyword.EditorID, IndexedFormKind.Keyword);
        Keywords[keyword.FormKey] = keyword;
    }

    private void Add(FormKey formKey, string? editorId, IndexedFormKind kind)
    {
        var indexed = new IndexedForm(formKey, editorId, kind);
        _byFormKey[formKey] = indexed;
        _knownPlugins.Add(formKey.ModKey.FileName.String);

        // Convert the origin FormKey into the runtime FormID used by SPID. This keeps
        // normal plugin load indices and FE/light-plugin indices distinct, so local IDs
        // from different plugins cannot be confused with one another.
        if (_runtimeFormIdsByMod.TryGetValue(formKey.ModKey, out var runtimeMapping) &&
            formKey.ID <= runtimeMapping.LocalIdMask)
        {
            uint runtimeFormId = runtimeMapping.Prefix | formKey.ID;
            if (!_byBareFormId.TryGetValue(runtimeFormId, out var formsWithId))
            {
                formsWithId = new List<IndexedForm>();
                _byBareFormId.Add(runtimeFormId, formsWithId);
            }
            formsWithId.Add(indexed);
        }

        if (string.IsNullOrWhiteSpace(editorId)) return;
        if (!_byEditorId.TryGetValue(editorId, out var values))
        {
            values = new List<IndexedForm>();
            _byEditorId.Add(editorId, values);
        }
        values.Add(indexed);
    }

    public bool TryResolveDistributedForm(
        DistributionKind kind,
        string raw,
        out FormKey formKey,
        out FormResolutionFailure failure)
    {
        var token = raw.Trim();
        if (token.Length == 0)
        {
            formKey = default;
            failure = new FormResolutionFailure
            {
                Kind = FormResolutionFailureKind.MalformedEditorId,
                Raw = raw,
                ExpectedKind = GetExpectedKind(kind)
            };
            return false;
        }

        if (TryParseFormKey(token, out var parsed))
        {
            if (!_byFormKey.TryGetValue(parsed, out var indexed))
            {
                formKey = default;
                failure = new FormResolutionFailure
                {
                    Kind = FormResolutionFailureKind.UnknownFormId,
                    Raw = raw,
                    HasFormKey = true,
                    FormKey = parsed,
                    ExpectedKind = GetExpectedKind(kind)
                };
                return false;
            }

            if (kind == DistributionKind.Keyword && string.IsNullOrWhiteSpace(indexed.EditorId))
            {
                formKey = default;
                failure = new FormResolutionFailure
                {
                    Kind = FormResolutionFailureKind.InvalidKeyword,
                    Raw = raw,
                    HasFormKey = true,
                    FormKey = parsed,
                    ExpectedKind = IndexedFormKind.Keyword,
                    ActualKind = indexed.Kind
                };
                return false;
            }

            if (IsCorrectDistributedType(kind, parsed))
            {
                formKey = parsed;
                failure = new FormResolutionFailure();
                return true;
            }

            formKey = default;
            failure = new FormResolutionFailure
            {
                Kind = FormResolutionFailureKind.MismatchingFormType,
                Raw = raw,
                HasFormKey = true,
                FormKey = parsed,
                ExpectedKind = GetExpectedKind(kind),
                ActualKind = indexed.Kind
            };
            return false;
        }

        FormResolutionFailure? bareFormFailure = null;
        if (TryParseBareFormId(token, out var bareFormId))
        {
            if (_byBareFormId.TryGetValue(bareFormId, out var formIdCandidates))
            {
                var candidate = formIdCandidates.FirstOrDefault(x => IsCorrectDistributedType(kind, x.FormKey));
                if (candidate is not null)
                {
                    if (kind == DistributionKind.Keyword && string.IsNullOrWhiteSpace(candidate.EditorId))
                    {
                        bareFormFailure = new FormResolutionFailure
                        {
                            Kind = FormResolutionFailureKind.InvalidKeyword,
                            Raw = raw,
                            HasFormKey = true,
                            FormKey = candidate.FormKey,
                            HasNumericFormId = true,
                            NumericFormId = bareFormId,
                            ExpectedKind = IndexedFormKind.Keyword,
                            ActualKind = candidate.Kind
                        };
                    }
                    else
                    {
                        formKey = candidate.FormKey;
                        failure = new FormResolutionFailure();
                        return true;
                    }
                }
                else
                {
                    var actual = formIdCandidates.First();
                    bareFormFailure = new FormResolutionFailure
                    {
                        Kind = FormResolutionFailureKind.MismatchingFormType,
                        Raw = raw,
                        HasFormKey = true,
                        FormKey = actual.FormKey,
                        HasNumericFormId = true,
                        NumericFormId = bareFormId,
                        ExpectedKind = GetExpectedKind(kind),
                        ActualKind = actual.Kind
                    };
                }
            }
            else
            {
                bareFormFailure = new FormResolutionFailure
                {
                    Kind = FormResolutionFailureKind.UnknownFormId,
                    Raw = raw,
                    HasNumericFormId = true,
                    NumericFormId = bareFormId,
                    ExpectedKind = GetExpectedKind(kind)
                };
            }
        }

        if (_byEditorId.TryGetValue(token, out var candidates))
        {
            var candidate = candidates.FirstOrDefault(x => IsCorrectDistributedType(kind, x.FormKey));
            if (candidate is not null)
            {
                formKey = candidate.FormKey;
                failure = new FormResolutionFailure();
                return true;
            }

            var actual = candidates.First();
            formKey = default;
            failure = new FormResolutionFailure
            {
                Kind = FormResolutionFailureKind.MismatchingFormType,
                Raw = raw,
                ExpectedKind = GetExpectedKind(kind),
                ActualKind = actual.Kind
            };
            return false;
        }

        formKey = default;
        failure = bareFormFailure ?? new FormResolutionFailure
        {
            Kind = FormResolutionFailureKind.UnknownEditorId,
            Raw = raw,
            ExpectedKind = GetExpectedKind(kind)
        };
        return false;
    }

    public ResolvedFormFilter ResolveFilter(string raw, out FormResolutionFailure failure)
    {
        var token = raw.Trim();
        var result = new ResolvedFormFilter { Raw = raw };

        if (token.Length == 0)
        {
            failure = new FormResolutionFailure
            {
                Kind = FormResolutionFailureKind.MalformedEditorId,
                Raw = raw
            };
            return result;
        }

        if (TryParseFormKey(token, out var formKey))
        {
            if (_byFormKey.TryGetValue(formKey, out var indexed))
            {
                if (indexed.Kind == IndexedFormKind.Keyword && string.IsNullOrWhiteSpace(indexed.EditorId))
                {
                    failure = new FormResolutionFailure
                    {
                        Kind = FormResolutionFailureKind.InvalidKeyword,
                        Raw = raw,
                        HasFormKey = true,
                        FormKey = formKey,
                        ActualKind = indexed.Kind
                    };
                    return result;
                }

                result.Candidates.Add(indexed);
                failure = new FormResolutionFailure();
                return result;
            }

            failure = new FormResolutionFailure
            {
                Kind = FormResolutionFailureKind.UnknownFormId,
                Raw = raw,
                HasFormKey = true,
                FormKey = formKey
            };
            return result;
        }

        FormResolutionFailure? bareFormFailure = null;
        if (TryParseBareFormId(token, out var bareFormId))
        {
            if (_byBareFormId.TryGetValue(bareFormId, out var formIdCandidates))
            {
                var validCandidates = formIdCandidates
                    .Where(x => x.Kind != IndexedFormKind.Keyword || !string.IsNullOrWhiteSpace(x.EditorId))
                    .ToArray();

                if (validCandidates.Length > 0)
                {
                    result.Candidates.AddRange(validCandidates);
                    failure = new FormResolutionFailure();
                    return result;
                }

                var invalid = formIdCandidates.First();
                bareFormFailure = new FormResolutionFailure
                {
                    Kind = FormResolutionFailureKind.InvalidKeyword,
                    Raw = raw,
                    HasFormKey = true,
                    FormKey = invalid.FormKey,
                    HasNumericFormId = true,
                    NumericFormId = bareFormId,
                    ActualKind = invalid.Kind
                };
            }
            else
            {
                bareFormFailure = new FormResolutionFailure
                {
                    Kind = FormResolutionFailureKind.UnknownFormId,
                    Raw = raw,
                    HasNumericFormId = true,
                    NumericFormId = bareFormId
                };
            }
        }

        if (LooksLikePluginName(token))
        {
            if (_knownPlugins.Contains(token))
            {
                failure = new FormResolutionFailure();
                return new ResolvedFormFilter
                {
                    Raw = raw,
                    IsPlugin = true,
                    PluginName = token
                };
            }

            failure = new FormResolutionFailure
            {
                Kind = FormResolutionFailureKind.UnknownPlugin,
                Raw = raw,
                PluginName = token
            };
            return result;
        }

        if (_byEditorId.TryGetValue(token, out var candidates))
        {
            result.Candidates.AddRange(candidates);
            failure = new FormResolutionFailure();
            return result;
        }

        failure = bareFormFailure ?? new FormResolutionFailure
        {
            Kind = FormResolutionFailureKind.UnknownEditorId,
            Raw = raw
        };
        return result;
    }

    public string? GetEditorId(FormKey formKey)
    {
        return _byFormKey.TryGetValue(formKey, out var value) ? value.EditorId : null;
    }

    public IndexedFormKind GetKind(FormKey formKey)
    {
        return _byFormKey.TryGetValue(formKey, out var value) ? value.Kind : IndexedFormKind.Unknown;
    }

    private static IndexedFormKind GetExpectedKind(DistributionKind kind)
    {
        return kind switch
        {
            DistributionKind.Keyword => IndexedFormKind.Keyword,
            DistributionKind.Spell => IndexedFormKind.Spell,
            DistributionKind.Perk => IndexedFormKind.Perk,
            DistributionKind.Shout => IndexedFormKind.Shout,
            DistributionKind.Package => IndexedFormKind.Package,
            DistributionKind.Item => IndexedFormKind.Item,
            DistributionKind.Outfit => IndexedFormKind.Outfit,
            DistributionKind.SleepOutfit => IndexedFormKind.Outfit,
            DistributionKind.Faction => IndexedFormKind.Faction,
            DistributionKind.Skin => IndexedFormKind.Armor,
            _ => IndexedFormKind.Unknown
        };
    }

    private bool IsCorrectDistributedType(DistributionKind kind, FormKey key)
    {
        return kind switch
        {
            DistributionKind.Keyword => Keywords.ContainsKey(key),
            DistributionKind.Spell => Spells.ContainsKey(key),
            DistributionKind.Perk => Perks.ContainsKey(key),
            DistributionKind.Shout => Shouts.ContainsKey(key),
            DistributionKind.Package => Packages.ContainsKey(key) || FormListItems.ContainsKey(key),
            DistributionKind.Item => Items.ContainsKey(key),
            DistributionKind.Outfit => Outfits.ContainsKey(key),
            DistributionKind.SleepOutfit => Outfits.ContainsKey(key),
            DistributionKind.Faction => Factions.ContainsKey(key),
            DistributionKind.Skin => Armors.ContainsKey(key),
            _ => false
        };
    }

    internal static bool IsBareEditorId(string raw)
    {
        var token = raw.Trim();
        if (token.Length == 0 || token.Any(char.IsWhiteSpace)) return false;
        if (token.Contains('~') || token.Contains(':') || token.Contains('|') || token.Contains(',')) return false;
        if (LooksLikePluginName(token)) return false;

        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    internal static bool TryParseBareFormId(string raw, out uint formId)
    {
        formId = default;
        var token = raw.Trim();
        if (!token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return false;

        var idPart = token[2..];
        if (idPart.Length == 0 || idPart.Length > 8) return false;
        if (!uint.TryParse(idPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)) return false;

        formId = parsed;
        return true;
    }

    internal static bool TryParseFormKey(string raw, out FormKey formKey)
    {
        formKey = default;
        var token = raw.Trim();
        if (token.Length == 0) return false;

        string? idPart = null;
        string? pluginPart = null;

        int tilde = token.IndexOf('~');
        if (tilde > 0 && tilde < token.Length - 1)
        {
            idPart = token[..tilde];
            pluginPart = token[(tilde + 1)..];
        }
        else
        {
            int colon = token.IndexOf(':');
            if (colon > 0 && colon < token.Length - 1)
            {
                idPart = token[..colon];
                pluginPart = token[(colon + 1)..];
            }
        }

        if (idPart is null || pluginPart is null) return false;
        idPart = idPart.Trim();
        pluginPart = pluginPart.Trim();
        if (idPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) idPart = idPart[2..];
        if (!uint.TryParse(idPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var localId)) return false;
        if (localId > 0x00FFFFFF) return false;

        if (!ModKey.TryFromNameAndExtension(pluginPart, out var modKey)) return false;
        formKey = new FormKey(modKey, localId);
        return true;
    }

    private readonly record struct RuntimeFormIdMapping(uint Prefix, uint LocalIdMask);

    private static bool LooksLikePluginName(string token)
    {
        if (token.Contains('~') || token.Contains(':')) return false;

        return token.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
               token.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
               token.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);
    }
}
