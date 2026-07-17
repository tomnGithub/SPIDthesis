using Mutagen.Bethesda.WPF.Reflection.Attributes;

namespace SPIDSynthesisPatcher;

public record Settings
{
    [SettingName("Search subfolders in Data")]
    [Tooltip("SPID configurations are normally found in the Skyrim Data folder. Enable this to also read _DISTR.ini files in subfolders.")]
    public bool SearchSubdirectories { get; set; } = true;

    [SettingName("Distribute keywords")]
    public bool EnableKeywords { get; set; } = true;

    [SettingName("Distribute spells")]
    public bool EnableSpells { get; set; } = true;

    [SettingName("Distribute perks")]
    public bool EnablePerks { get; set; } = true;

    [SettingName("Materialize inherited NPC lists")]
    [Tooltip("When an NPC inherits Keywords or Spell List from a template, copy the effective inherited list onto the NPC and clear only that template flag before adding records. This prevents the new entries from being ignored by the game.")]
    public bool MaterializeInheritedLists { get; set; } = true;

    [SettingName("Patch the Player NPC")]
    [Tooltip("Disabled by default. The Player base record is 0x000007 in Skyrim.esm.")]
    public bool PatchPlayer { get; set; } = false;

    [SettingName("Chance seed")]
    [Tooltip("Used for normal chance filters. Rules using SPID's trailing ! deterministic marker ignore this value.")]
    public int RandomSeed { get; set; } = 1337;

    [SettingName("Ignored INI file names")]
    [Tooltip("File names only, for example MyMod_DISTR.ini. Matching is case-insensitive.")]
    public List<string> IgnoredIniFiles { get; set; } = new();

    [SettingName("Ignored NPC source plugins")]
    [Tooltip("Plugin file names, for example SomeFollower.esp. Matching is case-insensitive.")]
    public List<string> IgnoredNpcPlugins { get; set; } = new();

    [SettingName("Verbose rule logging")]
    public bool VerboseLogging { get; set; } = false;
}
