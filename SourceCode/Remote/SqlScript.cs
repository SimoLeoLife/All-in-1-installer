namespace AllInOneInstaller.Remote;

/// <summary>One statement of a script and the line (from 1) it starts on.</summary>
public sealed record SqlStatement(string Text, int Line);

/// <summary>Splits a script into statements like the mariadb client: on ';' outside quotes and comments, with DELIMITER.</summary>
public static class SqlScript
{
    public static List<SqlStatement> Split(string script)
    {
        var statements = new List<SqlStatement>();
        string delimiter = ";";
        int start = 0, line = 1, startLine = 1;
        int i = 0;
        // Only whitespace and comments so far: such a statement is left out, and DELIMITER is only seen there.
        bool empty = true;

        void Add(int end)
        {
            string text = script[start..end].Trim();
            if (!empty && text.Length > 0) statements.Add(new SqlStatement(text, startLine));
            empty = true;
        }

        while (i < script.Length)
        {
            char c = script[i];
            if (empty && (char.IsWhiteSpace(c) || c == ';'))
            {
                if (c == '\n') line++;
                i++;
                start = i;
                startLine = line;
                continue;
            }
            if (empty && IsDelimiterCommand(script, i))
            {
                int end = script.IndexOf('\n', i);
                if (end < 0) end = script.Length;
                string value = script[(i + "DELIMITER".Length)..end].Trim();
                if (value.Length > 0) delimiter = value;
                i = end;
                start = i;
                continue;
            }
            if (c == '\'' || c == '"' || c == '`')
            {
                empty = false;
                i = SkipQuoted(script, i, ref line);
                continue;
            }
            if (c == '#' || (c == '-' && i + 1 < script.Length && script[i + 1] == '-' && (i + 2 == script.Length || char.IsWhiteSpace(script[i + 2]))))
            {
                int end = script.IndexOf('\n', i);
                i = end < 0 ? script.Length : end;
                continue;
            }
            if (c == '/' && i + 1 < script.Length && script[i + 1] == '*')
            {
                // /*! ... */ is executed by MariaDB, so it counts as content.
                if (i + 2 < script.Length && script[i + 2] == '!') empty = false;
                int end = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? script.Length : end + 2;
                line += script.AsSpan(i, end - i).Count('\n');
                i = end;
                continue;
            }
            if (string.CompareOrdinal(script, i, delimiter, 0, delimiter.Length) == 0)
            {
                Add(i);
                i += delimiter.Length;
                start = i;
                startLine = line;
                continue;
            }
            if (c == '\n') line++;
            if (!char.IsWhiteSpace(c)) empty = false;
            i++;
        }
        Add(script.Length);
        return statements;
    }

    private static bool IsDelimiterCommand(string script, int i) =>
        string.Compare(script, i, "DELIMITER", 0, "DELIMITER".Length, StringComparison.OrdinalIgnoreCase) == 0 &&
        i + "DELIMITER".Length < script.Length && script[i + "DELIMITER".Length] is ' ' or '\t';

    // Returns the index after the closing quote. Backslash escapes, and a doubled quote is a quote.
    private static int SkipQuoted(string script, int i, ref int line)
    {
        char quote = script[i++];
        while (i < script.Length)
        {
            char c = script[i];
            if (c == '\n') line++;
            if (c == '\\' && quote != '`')
            {
                if (i + 1 < script.Length && script[i + 1] == '\n') line++;
                i += 2;
                continue;
            }
            if (c == quote)
            {
                if (i + 1 < script.Length && script[i + 1] == quote) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return i;
    }
}
