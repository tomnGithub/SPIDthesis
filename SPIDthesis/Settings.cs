using Mutagen.Bethesda.WPF.Reflection.Attributes;

namespace SPIDthesis;

public record Settings
{
    [SettingName("Look in SPIDthesisConverted Folder")]
    [Tooltip("Also read *_DISTR.ini files from Data\\SPIDthesisConverted. Files in the Data root take precedence when the same file name exists in both locations.")]
    public bool LookInConvertedFolder { get; set; } = true;

    [SettingName("Move INIs processed by SPIDthesis to SPIDthesisConverted")]
    [Tooltip("Disabled by default. After a successful patch run, move every *_DISTR.ini file that SPIDthesis successfully opened and processed from the Data root into Data\\SPIDthesisConverted. Files that could not be read are not moved. Existing files with the same name are replaced. Files already in that folder are left in place.")]
    public bool MoveProcessedIniFiles { get; set; } = false;

    [SettingName("Only read listed INI files")]
    [Tooltip("When enabled, only _DISTR.ini files named in the list below are read. Matching is case-insensitive. An empty list reads no INIs.")]
    public bool OnlyReadListedIniFiles { get; set; } = false;

    [SettingName("INI files to read")]
    [Tooltip("Enter one file name per row, for example ThrowableWeaponsSKSE_NPCs_DISTR.ini. File names are matched case-insensitively in the Data root and, when enabled, Data\\SPIDthesisConverted.")]
    public List<string> IncludedIniFiles { get; set; } = new();

    [SettingName("Use chance distributions")]
    [Tooltip("Enabled by default. When enabled, rules with an explicit chance field use their configured rolls. When disabled, those rules are skipped entirely.")]
    public bool UseChanceDistribution { get; set; } = true;

    [SettingName("Extra chance logging")]
    [Tooltip("Disabled by default. After the normal run output, list every parsed INI line with an explicit chance field and the source plugin of its distributed form.")]
    public bool ExtraChanceLogging { get; set; } = false;

    [SettingName("Distribute keywords")]
    public bool EnableKeywords { get; set; } = true;

    [SettingName("Distribute spells")]
    public bool EnableSpells { get; set; } = true;

    [SettingName("Distribute perks")]
    public bool EnablePerks { get; set; } = true;

    [SettingName("Distribute shouts")]
    public bool EnableShouts { get; set; } = true;

    [SettingName("Distribute packages")]
    [Tooltip("Adds Package rules to NPC AI package lists. A FormList value sets one of SPID's five package-list slots using the sixth INI field as the slot index.")]
    public bool EnablePackages { get; set; } = true;

    [SettingName("Distribute factions")]
    [Tooltip("Adds matching Faction rules at rank 1.")]
    public bool EnableFactions { get; set; } = true;

    [SettingName("Distribute items")]
    [Tooltip("Adds Item rules to matching NPC inventories. Item count ranges are selected independently for each patch run.")]
    public bool EnableItems { get; set; } = true;

    [SettingName("Distribute outfits")]
    [Tooltip("Assigns the first matching Outfit or FinalOutfit rule to each NPC's default outfit.")]
    public bool EnableOutfits { get; set; } = true;

    [SettingName("Distribute sleep outfits")]
    [Tooltip("Assigns the first matching SleepOutfit rule to each NPC's sleeping outfit.")]
    public bool EnableSleepOutfits { get; set; } = true;

    [SettingName("Distribute skins")]
    [Tooltip("Assigns the first matching Skin rule to each NPC's Worn Armor (skin) field.")]
    public bool EnableSkins { get; set; } = true;

    [SettingName("Ignored INI file names")]
    [Tooltip("File names only, for example MyMod_DISTR.ini. Matching is case-insensitive.")]
    public List<string> IgnoredIniFiles { get; set; } = new();
}
