using System.ComponentModel;
using System.Text.Json;
using DbMaster.Core;
using ModelContextProtocol.Server;

namespace DbMaster.Server.Tools;

/// <summary>
/// 数据库管理 MCP 工具集 — 提供连接管理、查询、表结构浏览等能力。
/// 
/// 使用适配器模式支持多种数据库，通过 dbType 参数显式指定或自动检测。
/// </summary>
[McpServerToolType]
public sealed class DatabaseTools
{
    private readonly ConnectionManager _cm;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DatabaseTools(ConnectionManager connectionManager)
    {
        _cm = connectionManager;
    }

    // ================================================================
    // 发现工具
    // ================================================================

    [McpServerTool(Name = "db_list_supported_types"),
     Description("Lists all supported database types with connection string examples. Call this first to see available options.")]
    public static string DbListSupportedTypes()
    {
        return """
            Supported database types (use with db_connect's dbType parameter):
            
            sqlite     — SQLite (file-based, zero config)
                        Example: Data Source=:memory:  or  Data Source=path/to/db.sqlite
            
            mysql      — MySQL / MariaDB
                        Example: Server=host;Port=3306;Database=db;User=root;Password=xxx;Pooling=true;MinPoolSize=1
            
            postgresql — PostgreSQL
                        Example: Host=host;Port=5432;Database=db;Username=postgres;Password=xxx;Pooling=true;MinPoolSize=1
            
            sqlserver  — SQL Server
                        Example: Server=host;Database=db;User Id=sa;Password=xxx;TrustServerCertificate=True;Pooling=true;MinPoolSize=1
            
            auto       — Auto-detect from connection string keywords (default)
            
            💡 Tip: Add 'Pooling=true;MinPoolSize=1' for remote databases to reuse TCP connections.
            """;
    }

    // ================================================================
    // 连接管理
    // ================================================================

    [McpServerTool(Name = "db_connect"),
     Description("Connect to a database and assign an alias. Use db_list_supported_types first to see options.")]
    public async Task<string> DbConnect(
        [Description("Database connection string")] string connectionString,
        [Description("Short alias for this connection, e.g. 'prod' or 'dev'")] string alias,
        [Description("Database type: 'auto' (default), 'sqlite', 'mysql', 'postgresql', or 'sqlserver'")]
        string dbType = "auto",
        CancellationToken ct = default)
    {
        try
        {
            var detectedType = await _cm.ConnectAsync(alias, connectionString, dbType, ct);
            return $"Connected to {detectedType} database as '{alias}'.";
        }
        catch (Exception ex)
        {
            return $"Connection failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "db_disconnect"), Description("Disconnect from a database by alias.")]
    public string DbDisconnect(
        [Description("Connection alias to disconnect")] string alias)
    {
        return _cm.Disconnect(alias)
            ? $"Disconnected '{alias}'."
            : $"Alias '{alias}' not found.";
    }

    [McpServerTool(Name = "db_list_connections"), Description("List all active database connections.")]
    public string DbListConnections()
    {
        var connections = _cm.ListConnections();
        if (connections.Count == 0)
            return "No active connections. Use db_connect to connect.";

        return "Active connections:\n" + string.Join("\n", connections.Select(c =>
            $"  [{c.Alias}] {c.DbType} (connected {c.ConnectedAt:HH:mm:ss}, last used {c.LastAccess:HH:mm:ss})"));
    }

    // ================================================================
    // 查询操作
    // ================================================================

    [McpServerTool(Name = "db_execute_query"),
     Description("Execute a SELECT query on a connected database. Returns results as JSON.")]
    public async Task<string> DbExecuteQuery(
        [Description("Connection alias")] string alias,
        [Description("SQL SELECT query to execute")] string sql,
        [Description("Maximum rows to return. Default 100.")] int maxRows = 100,
        CancellationToken ct = default)
    {
        var adapter = _cm.GetAdapter(alias);
        if (adapter is null)
            return $"Error: Connection '{alias}' not found or timed out. Use db_list_connections to check.";

        var trimmed = sql.Trim();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase))
        {
            return "Error: Only SELECT, WITH, PRAGMA, and EXPLAIN queries are allowed. " +
                   "Use db_execute_command for write operations.";
        }

        try
        {
            var result = await adapter.QueryAsync(sql, maxRows, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return $"Query error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "db_execute_command"),
     Description("Execute a write command (INSERT, UPDATE, DELETE, DDL). Requires confirmation.")]
    public async Task<string> DbExecuteCommand(
        [Description("Connection alias")] string alias,
        [Description("SQL command to execute")] string sql,
        [Description("Must be 'CONFIRM' to execute write operations")] string confirm,
        CancellationToken ct = default)
    {
        if (confirm != "CONFIRM")
            return "Error: Set confirm='CONFIRM' to execute write operations.";

        var adapter = _cm.GetAdapter(alias);
        if (adapter is null)
            return $"Error: Connection '{alias}' not found.";

        // 检查危险操作
        var upper = sql.Trim().ToUpperInvariant();
        if ((upper.StartsWith("DROP") || upper.StartsWith("TRUNCATE")) && confirm != "I_KNOW_WHAT_I_AM_DOING")
            return "Error: DROP/TRUNCATE requires confirm='I_KNOW_WHAT_I_AM_DOING'.";

        try
        {
            var affected = await adapter.ExecuteAsync(sql, ct);
            return $"Command executed successfully. Rows affected: {affected}";
        }
        catch (Exception ex)
        {
            return $"Command error: {ex.Message}";
        }
    }

    // ================================================================
    // 表结构浏览
    // ================================================================

    [McpServerTool(Name = "db_list_tables"), Description("List all user tables in the connected database.")]
    public async Task<string> DbListTables(
        [Description("Connection alias")] string alias,
        CancellationToken ct = default)
    {
        var adapter = _cm.GetAdapter(alias);
        if (adapter is null) return $"Error: Connection '{alias}' not found.";

        try
        {
            var tables = await adapter.ListTablesAsync(ct);
            if (tables.Count == 0)
                return "No user tables found.";

            return "Tables:\n" + string.Join("\n", tables.Select(t =>
                $"  {t.Name} ({t.RowCount:N0} rows){(t.Comment is not null ? $" — {t.Comment}" : "")}"));
        }
        catch (Exception ex)
        {
            return $"Error listing tables: {ex.Message}";
        }
    }

    [McpServerTool(Name = "db_describe_table"),
     Description("Get the full schema of a table: columns, types, keys, indexes, and create SQL.")]
    public async Task<string> DbDescribeTable(
        [Description("Connection alias")] string alias,
        [Description("Table name to describe")] string tableName,
        CancellationToken ct = default)
    {
        var adapter = _cm.GetAdapter(alias);
        if (adapter is null) return $"Error: Connection '{alias}' not found.";

        try
        {
            var schema = await adapter.DescribeTableAsync(tableName, ct);

            var lines = new List<string>
            {
                $"Table: {schema.TableName}",
                new string('-', 60),
                $"{"Column",-20} {"Type",-15} {"Nullable",-10} {"PK",-5} {"Default",-15}",
                new string('-', 60),
            };

            foreach (var col in schema.Columns)
            {
                lines.Add(
                    $"{col.Name,-20} {col.DataType,-15} {(col.IsNullable ? "YES" : "NO"),-10} " +
                    $"{(col.IsPrimaryKey ? "YES" : "NO"),-5} {col.DefaultValue?.ToString() ?? "(null)",-15}");
            }

            lines.Add(new string('-', 60));

            if (schema.PrimaryKeys.Count > 0)
                lines.Add($"Primary Key: {string.Join(", ", schema.PrimaryKeys)}");

            if (schema.ForeignKeys.Count > 0)
            {
                lines.Add("Foreign Keys:");
                foreach (var fk in schema.ForeignKeys)
                    lines.Add($"  {fk.ColumnName} → {fk.ReferencedTable}.{fk.ReferencedColumn}");
            }

            if (schema.Indexes.Count > 0)
            {
                lines.Add("Indexes:");
                foreach (var idx in schema.Indexes)
                    lines.Add($"  {idx.Name} ({string.Join(", ", idx.Columns)}) {(idx.IsUnique ? "UNIQUE" : "")}");
            }

            if (!string.IsNullOrEmpty(schema.CreateSql))
                lines.Add($"\nCreate SQL:\n{schema.CreateSql}");

            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error describing table: {ex.Message}";
        }
    }

    // ================================================================
    // 统计
    // ================================================================

    [McpServerTool(Name = "db_table_stats"), Description("Get row counts and basic statistics for all tables.")]
    public async Task<string> DbTableStats(
        [Description("Connection alias")] string alias,
        CancellationToken ct = default)
    {
        var adapter = _cm.GetAdapter(alias);
        if (adapter is null) return $"Error: Connection '{alias}' not found.";

        try
        {
            var tables = await adapter.ListTablesAsync(ct);

            if (tables.Count == 0) return "No tables found.";

            var totalRows = tables.Sum(t => (long)t.RowCount);

            var lines = new List<string>
            {
                $"Database Stats ({adapter.DbType}):",
                new string('-', 40),
            };

            foreach (var t in tables.OrderByDescending(t => t.RowCount))
                lines.Add($"  {t.Name,-30} {t.RowCount,8:N0} rows");

            lines.Add(new string('-', 40));
            lines.Add($"  {"Total",-30} {totalRows,8:N0} rows across {tables.Count} tables");

            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error getting stats: {ex.Message}";
        }
    }
}
