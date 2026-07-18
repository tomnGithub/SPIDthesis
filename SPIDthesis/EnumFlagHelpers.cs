namespace SPIDthesis;

internal static class EnumFlagHelpers
{
    public static bool HasNamedFlag<TEnum>(TEnum value, string flagName)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(flagName, true, out var flag)) return false;
        return value.HasFlag(flag);
    }
}
