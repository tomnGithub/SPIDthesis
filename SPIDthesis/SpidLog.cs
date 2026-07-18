namespace SPIDthesis;

internal static class SpidLog
{
    private const int HeaderWidth = 50;

    public static void Header(string name)
    {
        string value = name.Trim();
        int rightFill = Math.Max(0, HeaderWidth - value.Length - 5);
        Info($"### {value} {new string('#', rightFill)}");
    }

    public static void Info(string message)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss:fff}] {message}");
    }

    public static void Warn(string message)
    {
        Info(message);
    }
}
