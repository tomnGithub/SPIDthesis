using System.Globalization;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace SPIDThesis;

internal sealed class RecordIndex
{
    private readonly Dictionary<string, List<IndexedForm>> _byEditorId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<FormKey, IndexedForm> _byFormKey = new();

    public Dictionary<FormKey, IKeywordGetter> Keywords { get; } = new();
    public Dictionary<FormKey, ISpellGetter> Spells { get; } = new();
    public Dictionary<FormKey, IPerkGetter> Perks { get; } = new();
    public Dictionary<FormKey, IRaceGetter> Races { get; } = new();
    public Dictionary<FormKey, HashSet<FormKey>> FormListItems { get; } = new();

    private RecordIndex()
    {
    }

    public static RecordIndex Build(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
    {
        var index = new RecordIndex();

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
        }

        foreach (var record in state.LoadOrder.PriorityOrder.Armor().WinningOverrides())
        {
            index.Add(record.FormKey, record.EditorID, IndexedFormKind.Armor);
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

        if (string.IsNullOrWhiteSpace(editorId)) return;
        if (!_byEditorId.TryGetValue(editorId, out var values))
        {
            values = new List<IndexedForm>();
            _byEditorId.Add(editorId, values);
        }
        values.Add(indexed);
    }

    public bool TryResolveDistributedForm(DistributionKind kind, string raw, out FormKey formKey)
    {
        if (TryParseFormKey(raw, out var parsed) && IsCorrectDistributedType(kind, parsed))
        {
            formKey = parsed;
            return true;
        }

        if (_byEditorId.TryGetValue(raw.Trim(), out var candidates))
        {
            var candidate = candidates.FirstOrDefault(x => IsCorrectDistributedType(kind, x.FormKey));
            if (candidate is not null)
            {
                formKey = candidate.FormKey;
                return true;
            }
        }

        formKey = default;
        return false;
    }

    public ResolvedFormFilter ResolveFilter(string raw)
    {
        var token = raw.Trim();
        var result = new ResolvedFormFilter { Raw = raw };

        // Explicit form references also end in .esm/.esp/.esl. They must be parsed before
        // checking for a plugin-only filter, otherwise 0x01BCC0~Skyrim.esm is incorrectly
        // treated as the literal plugin name "0x01BCC0~Skyrim.esm" and can never match.
        if (TryParseFormKey(token, out var formKey))
        {
            if (_byFormKey.TryGetValue(formKey, out var indexed))
            {
                result.Candidates.Add(indexed);
            }
            else
            {
                // Keep a concrete FormKey candidate even for record types not indexed by this patcher.
                result.Candidates.Add(new IndexedForm(formKey, null, IndexedFormKind.Unknown));
            }
            return result;
        }

        if (LooksLikePluginName(token))
        {
            return new ResolvedFormFilter
            {
                Raw = raw,
                IsPlugin = true,
                PluginName = token
            };
        }

        if (_byEditorId.TryGetValue(token, out var candidates))
        {
            result.Candidates.AddRange(candidates);
        }

        return result;
    }

    public string? GetEditorId(FormKey formKey)
    {
        return _byFormKey.TryGetValue(formKey, out var value) ? value.EditorId : null;
    }

    private bool IsCorrectDistributedType(DistributionKind kind, FormKey key)
    {
        return kind switch
        {
            DistributionKind.Keyword => Keywords.ContainsKey(key),
            DistributionKind.Spell => Spells.ContainsKey(key),
            DistributionKind.Perk => Perks.ContainsKey(key),
            _ => false
        };
    }

    internal static bool IsBareEditorId(string raw)
    {
        var token = raw.Trim();
        if (token.Length == 0 || token.Any(char.IsWhiteSpace)) return false;
        if (token.Contains('~') || token.Contains(':') || token.Contains('|') || token.Contains(',')) return false;
        if (LooksLikePluginName(token)) return false;

        // A malformed explicit hexadecimal reference should fail closed rather than becoming a new KYWD.
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return false;

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

    private static bool LooksLikePluginName(string token)
    {
        // A plugin-only filter is just a filename. Explicit form references contain a separator.
        if (token.Contains('~') || token.Contains(':')) return false;

        return token.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
               token.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
               token.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);
    }
}
