using Mutagen.Bethesda.WPF.Reflection.Attributes;

namespace SPIDThesis;

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

    [SettingName("Enable template handling")]
    [Tooltip("Disabled by default. When enabled, match against reachable NPC/LVLN templates and use effective inherited Keyword and Spell List data. When disabled, only the winning NPC record is matched and additions are written directly even if its template flags mean the game may ignore them.")]
    public bool EnableTemplateHandling { get; set; } = false;

    [SettingName("Materialize inherited NPC lists")]
    [Tooltip("Only applies when template handling is enabled. Copies effective inherited Keyword or Spell List data onto the NPC and clears only the relevant template flag before adding records.")]
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
