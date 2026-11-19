using System.IO;

namespace CommonClassLibrary;

/// <summary>SQL Server identifier and literal quoting used by the standalone EF6 generator.</summary>
public static class SqlProviders
{
    private static void RequireSqlServer(string providerId)
    {
        if (!string.Equals(providerId, "sqlserver", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("This standalone generator supports SQL Server only.", nameof(providerId));
    }

    public static string QuoteIdentifier(string providerId, string name)
    {
        RequireSqlServer(providerId);
        if (string.IsNullOrEmpty(name) || name.IndexOf('\0') >= 0)
            throw new ArgumentException("An SQL identifier must be non-empty and contain no NUL.", nameof(name));
        return "[" + name.Replace("]", "]]") + "]";
    }

    public static string QuoteStringLiteral(string providerId, string value)
    {
        RequireSqlServer(providerId);
        return "'" + value.Replace("'", "''") + "'";
    }
}

public static class PocoProjectIntegration
{
    internal static string? LocalPath(string folder, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['*', '?', ';', '$', '%', '@']) >= 0)
            return null;
        try
        {
            return Path.GetFullPath(value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar), folder);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string? localPath(string folder, string value) => LocalPath(folder, value);
}

/// <summary>The console owns the explicit destination. No background timer or application state is used.</summary>
public static class TemporaryStorage
{
    public static void RequirePersistentDestination(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Choose a destination folder.", nameof(path));
        if (File.Exists(Path.GetFullPath(path)))
            throw new ArgumentException("A file occupies the selected destination folder.", nameof(path));
    }
}
