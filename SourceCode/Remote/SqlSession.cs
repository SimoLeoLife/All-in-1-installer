using System.Diagnostics;
using System.Globalization;
using MySqlConnector;

namespace AllInOneInstaller.Remote;

public sealed record SqlColumn(string Name, string DataType)
{
    public bool IsBinary => DataType.Contains("BLOB", StringComparison.OrdinalIgnoreCase) || DataType.Contains("BINARY", StringComparison.OrdinalIgnoreCase);
}

public sealed record SqlTableInfo(string Name, bool IsView, long? Rows, long? Bytes, string? Engine);

/// <summary>What one statement returned: rows (also none) when it is a query, otherwise how many rows it changed.</summary>
public sealed class SqlResult
{
    public required string Statement { get; init; }
    public IReadOnlyList<SqlColumn>? Columns { get; init; }
    public List<object?[]> Rows { get; } = [];
    /// <summary>More rows were returned than were kept.</summary>
    public bool Truncated { get; set; }
    public long Affected { get; set; }
    public TimeSpan Elapsed { get; set; }
}

/// <summary>The results of a script up to the first error, which stops it like the mariadb client does.</summary>
public sealed record SqlRun(List<SqlResult> Results, int Statements, string? Error, int ErrorLine, bool Cancelled);

/// <summary>
/// MariaDB over an SSH tunnel to 127.0.0.1:3306 on the server, the way HeidiSQL connects. One connection runs the
/// query editor (so USE and variables last), the other one the database tree, table data and edits.
/// </summary>
public sealed class SqlSession : IAsyncDisposable
{
    private readonly ServerConnection.PortForward _forward;
    private readonly MySqlConnection _query;
    private readonly MySqlConnection _browse;
    private readonly SemaphoreSlim _queryLock = new(1, 1);
    private readonly SemaphoreSlim _browseLock = new(1, 1);

    public string User { get; }
    // MariaDB announces itself as "5.5.5-10.11.x-MariaDB" for old MySQL clients.
    public string ServerVersion => _browse.ServerVersion.StartsWith("5.5.5-", StringComparison.Ordinal) ? _browse.ServerVersion[6..] : _browse.ServerVersion;

    private SqlSession(ServerConnection.PortForward forward, MySqlConnection query, MySqlConnection browse, string user)
    {
        _forward = forward;
        _query = query;
        _browse = browse;
        User = user;
    }

    public static async Task<SqlSession> OpenAsync(ServerConnection server, string user, string password, int port = 3306,
        CancellationToken token = default)
    {
        ServerConnection.PortForward forward = server.ForwardLocalPort("127.0.0.1", port);
        var builder = new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1",
            Port = (uint)forward.LocalPort,
            UserID = user,
            Password = password,
            // The tunnel is encrypted already.
            SslMode = MySqlSslMode.None,
            Pooling = false,
            ConnectionTimeout = 20,
            DefaultCommandTimeout = 0,
            AllowUserVariables = true,
            AllowZeroDateTime = true,
            // Changed rows, as the mariadb client reports them, rather than matched rows.
            UseAffectedRows = true,
            // Show the values as they are stored: TINYINT(1) as a number, CHAR(36) as text.
            TreatTinyAsBoolean = false,
            GuidFormat = MySqlGuidFormat.None,
            AllowLoadLocalInfile = false
        };
        var query = new MySqlConnection(builder.ConnectionString);
        var browse = new MySqlConnection(builder.ConnectionString);
        try
        {
            await query.OpenAsync(token);
            await browse.OpenAsync(token);
        }
        catch
        {
            await query.DisposeAsync();
            await browse.DisposeAsync();
            forward.Dispose();
            throw;
        }
        return new SqlSession(forward, query, browse, user);
    }

    public static string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";

    /// <summary>A value as the grids show it, binary data cut off after <paramref name="maxBytes"/>. NULL is null.</summary>
    public static string? Display(object? value, SqlColumn? column = null, int maxBytes = 64)
    {
        switch (value)
        {
            case null or DBNull:
                return null;
            case byte[] bytes:
                return "0x" + Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, maxBytes)) + (bytes.Length > maxBytes ? $"… ({bytes.Length} bytes)" : "");
            case DateTime date:
                if (column?.DataType.Equals("DATE", StringComparison.OrdinalIgnoreCase) == true) return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return date.ToString(date.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
            case MySqlDateTime date when !date.IsValidDateTime:
                return column?.DataType.Equals("DATE", StringComparison.OrdinalIgnoreCase) == true
                    ? $"{date.Year:0000}-{date.Month:00}-{date.Day:00}"
                    : $"{date.Year:0000}-{date.Month:00}-{date.Day:00} {date.Hour:00}:{date.Minute:00}:{date.Second:00}";
            case MySqlDateTime date:
                return Display(date.GetDateTime(), column);
            case TimeSpan time:
                return (time < TimeSpan.Zero ? "-" : "") + $"{(int)Math.Abs(time.TotalHours):00}:{Math.Abs(time.Minutes):00}:{Math.Abs(time.Seconds):00}";
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return value.ToString();
        }
    }

    public Task<List<string>> DatabasesAsync(CancellationToken token = default) => BrowseAsync(async () =>
    {
        var names = new List<string>();
        await using var command = new MySqlCommand("SHOW DATABASES", _browse);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) names.Add(reader.GetString(0));
        return names;
    });

    public Task<List<SqlTableInfo>> TablesAsync(string database, CancellationToken token = default) => BrowseAsync(async () =>
    {
        var tables = new List<SqlTableInfo>();
        await using var command = new MySqlCommand(
            "SELECT TABLE_NAME, TABLE_TYPE, TABLE_ROWS, DATA_LENGTH + INDEX_LENGTH, ENGINE FROM information_schema.TABLES " +
            "WHERE TABLE_SCHEMA = @db ORDER BY TABLE_NAME", _browse);
        command.Parameters.AddWithValue("@db", database);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            tables.Add(new SqlTableInfo(reader.GetString(0), reader.GetString(1) == "VIEW",
                reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2)),
                reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        return tables;
    });

    /// <summary>The primary key's columns; empty when the table has none, and then its rows cannot be edited.</summary>
    public Task<List<string>> PrimaryKeyAsync(string database, string table, CancellationToken token = default) => BrowseAsync(async () =>
    {
        var columns = new List<string>();
        await using var command = new MySqlCommand(
            "SELECT COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE " +
            "WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table AND CONSTRAINT_NAME = 'PRIMARY' ORDER BY ORDINAL_POSITION", _browse);
        command.Parameters.AddWithValue("@db", database);
        command.Parameters.AddWithValue("@table", table);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) columns.Add(reader.GetString(0));
        return columns;
    });

    /// <param name="filter">A WHERE condition as typed by the user, or empty.</param>
    public Task<SqlResult> TableDataAsync(string database, string table, string filter, string? orderBy, bool descending,
        long offset, int limit, CancellationToken token = default)
    {
        string sql = $"SELECT * FROM {Quote(database)}.{Quote(table)}" +
                     (string.IsNullOrWhiteSpace(filter) ? "" : $" WHERE {filter}") +
                     (orderBy is null ? "" : $" ORDER BY {Quote(orderBy)}{(descending ? " DESC" : "")}") +
                     $" LIMIT {offset}, {limit}";
        return BrowseAsync(() => ExecuteAsync(_browse, sql, limit, token));
    }

    public Task<SqlResult> ColumnsAsync(string database, string table, CancellationToken token = default) =>
        BrowseAsync(() => ExecuteAsync(_browse, $"SHOW FULL COLUMNS FROM {Quote(database)}.{Quote(table)}", int.MaxValue, token));

    public Task<string> CreateStatementAsync(string database, string table, CancellationToken token = default) => BrowseAsync(async () =>
    {
        SqlResult result = await ExecuteAsync(_browse, $"SHOW CREATE TABLE {Quote(database)}.{Quote(table)}", 1, token);
        return result.Rows.Count > 0 ? Display(result.Rows[0][1]) ?? "" : "";
    });

    /// <summary>Sets one cell of the row with this primary key. Returns the number of rows changed (0 when the value was the same).</summary>
    public Task<long> UpdateCellAsync(string database, string table, string column, string? value,
        IReadOnlyList<(string Column, object? Value)> key, CancellationToken token = default) => BrowseAsync(async () =>
    {
        await using var command = new MySqlCommand(
            $"UPDATE {Quote(database)}.{Quote(table)} SET {Quote(column)} = @value WHERE {KeyCondition(key)} LIMIT 1", _browse);
        command.Parameters.AddWithValue("@value", value);
        AddKeyParameters(command, key);
        return (long)await command.ExecuteNonQueryAsync(token);
    });

    public Task<long> DeleteRowsAsync(string database, string table, IReadOnlyList<IReadOnlyList<(string Column, object? Value)>> keys,
        CancellationToken token = default) => BrowseAsync(async () =>
    {
        await using MySqlTransaction transaction = await _browse.BeginTransactionAsync(token);
        long deleted = 0;
        foreach (var key in keys)
        {
            await using var command = new MySqlCommand($"DELETE FROM {Quote(database)}.{Quote(table)} WHERE {KeyCondition(key)} LIMIT 1", _browse, transaction);
            AddKeyParameters(command, key);
            deleted += await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        return deleted;
    });

    // NULL-safe, so a key part that is NULL still matches.
    private static string KeyCondition(IReadOnlyList<(string Column, object? Value)> key) =>
        string.Join(" AND ", key.Select((part, i) => $"{Quote(part.Column)} <=> @key{i}"));

    private static void AddKeyParameters(MySqlCommand command, IReadOnlyList<(string Column, object? Value)> key)
    {
        for (int i = 0; i < key.Count; i++) command.Parameters.AddWithValue($"@key{i}", key[i].Value ?? DBNull.Value);
    }

    /// <summary>The database the query editor uses (changed by USE).</summary>
    public async Task<string?> CurrentDatabaseAsync(CancellationToken token = default)
    {
        await _queryLock.WaitAsync(token);
        try
        {
            await using var command = new MySqlCommand("SELECT DATABASE()", _query);
            return await command.ExecuteScalarAsync(token) as string;
        }
        finally
        {
            _queryLock.Release();
        }
    }

    /// <summary>Runs the statements of <paramref name="script"/> one by one in <paramref name="database"/> and stops at the first error.</summary>
    /// <param name="progress">Gets the number of the statement that starts and the total.</param>
    public async Task<SqlRun> RunScriptAsync(string script, string? database, int maxRows, Action<int, int>? progress = null,
        CancellationToken token = default)
    {
        List<SqlStatement> statements = SqlScript.Split(script);
        var results = new List<SqlResult>();
        await _queryLock.WaitAsync(token);
        try
        {
            if (!string.IsNullOrEmpty(database) && database != _query.Database) await _query.ChangeDatabaseAsync(database, token);
            for (int i = 0; i < statements.Count; i++)
            {
                progress?.Invoke(i + 1, statements.Count);
                try
                {
                    results.AddRange(await ExecuteAllAsync(_query, statements[i].Text, maxRows, token));
                }
                catch (OperationCanceledException)
                {
                    return new SqlRun(results, statements.Count, $"Stopped at statement {i + 1} (line {statements[i].Line}).", statements[i].Line, Cancelled: true);
                }
                catch (MySqlException ex) when (token.IsCancellationRequested && ex.ErrorCode == MySqlErrorCode.QueryInterrupted)
                {
                    return new SqlRun(results, statements.Count, $"Stopped at statement {i + 1} (line {statements[i].Line}).", statements[i].Line, Cancelled: true);
                }
                catch (MySqlException ex)
                {
                    return new SqlRun(results, statements.Count,
                        $"SQL Error ({ex.Number}) in statement {i + 1} (line {statements[i].Line}): {ex.Message}", statements[i].Line, Cancelled: false);
                }
            }
            return new SqlRun(results, statements.Count, null, 0, Cancelled: false);
        }
        finally
        {
            _queryLock.Release();
        }
    }

    private async Task<T> BrowseAsync<T>(Func<Task<T>> action)
    {
        await _browseLock.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            _browseLock.Release();
        }
    }

    private static async Task<SqlResult> ExecuteAsync(MySqlConnection connection, string sql, int maxRows, CancellationToken token) =>
        (await ExecuteAllAsync(connection, sql, maxRows, token)).FirstOrDefault(r => r.Columns is not null)
        ?? new SqlResult { Statement = sql, Columns = [] };

    // A statement can return several result sets (CALL); each becomes a result. Without any, the affected rows are one.
    private static async Task<List<SqlResult>> ExecuteAllAsync(MySqlConnection connection, string sql, int maxRows, CancellationToken token)
    {
        var results = new List<SqlResult>();
        var watch = Stopwatch.StartNew();
        await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 0 };
        MySqlDataReader reader = await command.ExecuteReaderAsync(token);
        bool truncated = false;
        long affected = 0;
        try
        {
            do
            {
                if (reader.FieldCount == 0) continue;
                var columns = Enumerable.Range(0, reader.FieldCount).Select(i => new SqlColumn(reader.GetName(i), reader.GetDataTypeName(i))).ToList();
                var result = new SqlResult { Statement = sql, Columns = columns };
                while (await reader.ReadAsync(token))
                {
                    if (result.Rows.Count >= maxRows)
                    {
                        result.Truncated = truncated = true;
                        break;
                    }
                    var row = new object?[columns.Count];
                    for (int i = 0; i < row.Length; i++)
                    {
                        object value = reader.GetValue(i);
                        row[i] = value is DBNull ? null : value;
                    }
                    result.Rows.Add(row);
                }
                result.Elapsed = watch.Elapsed;
                results.Add(result);
                if (truncated) break;
            } while (await reader.NextResultAsync(token));
            if (!truncated) affected = Math.Max(reader.RecordsAffected, 0);
        }
        finally
        {
            // Reading the rest of a huge result would take as long as the query; stop it on the server instead.
            if (truncated)
            {
                try { command.Cancel(); } catch (Exception) { }
            }
            try
            {
                await reader.DisposeAsync();
            }
            catch (MySqlException) when (truncated) { }
        }
        if (results.Count == 0)
            results.Add(new SqlResult { Statement = sql, Affected = affected, Elapsed = watch.Elapsed });
        return results;
    }

    public async ValueTask DisposeAsync()
    {
        try { await _query.DisposeAsync(); } catch (Exception) { }
        try { await _browse.DisposeAsync(); } catch (Exception) { }
        _forward.Dispose();
    }
}
