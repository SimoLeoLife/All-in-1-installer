namespace AllInOneInstaller.Remote;

internal static class Sql
{
    // Backslash escaping, as MariaDB uses by default.
    public static string Literal(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
}
