using System.ComponentModel;
using System.Text.Encodings.Web;
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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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

    // ================================================================
    // Phase 5.2: EXPLAIN 执行计划分析
    // ================================================================

    [McpServerTool(Name = "db_explain_query"),
     Description("Analyze a SQL query's execution plan using EXPLAIN. Helps identify slow queries, missing indexes, and performance bottlenecks.")]
    public async Task<string> DbExplainQuery(
        [Description("Connection alias")] string alias,
        [Description("SQL SELECT query to analyze (do NOT include EXPLAIN prefix, tool adds it automatically)")] string sql,
        [Description("Maximum rows of plan output. Default 200.")] int maxRows = 200,
        CancellationToken ct = default)
    {
        var adapter = _cm.GetAdapter(alias);
        if (adapter is null)
            return $"Error: Connection '{alias}' not found.";

        var trimmed = sql.Trim();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
        {
            return "Error: Only SELECT/WITH queries can be explained. For DML execution, use db_execute_command.";
        }

        try
        {
            var result = await adapter.ExplainAsync(sql, maxRows, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return $"EXPLAIN error: {ex.Message}";
        }
    }

    // ================================================================
    // Phase 5.3: 导出数据
    // ================================================================

    /// <summary>获取导出基础目录：优先 DBMASTER_EXPORT_DIR 环境变量，其次当前目录</summary>
    private static string GetExportBaseDir() =>
        Environment.GetEnvironmentVariable("DBMASTER_EXPORT_DIR")
        ?? Directory.GetCurrentDirectory();

    /// <summary>解析导出路径：相对路径基于工作区根目录，绝对路径保持不变</summary>
    private static string ResolveExportPath(string filePath)
    {
        if (Path.IsPathRooted(filePath))
            return filePath;
        return Path.Combine(GetExportBaseDir(), filePath);
    }

    [McpServerTool(Name = "db_export_data"),
     Description("Export query results to a JSON or CSV file. Relative paths resolve to the VS Code workspace root. Useful for data analysis, sharing, or backup.")]
    public async Task<string> DbExportData(
        [Description("Connection alias")] string alias,
        [Description("SQL SELECT query to export")] string sql,
        [Description("Output file path (e.g., 'export.json' for workspace root, or absolute path like 'C:/data.csv')")] string filePath,
        [Description("Export format: 'json' or 'csv'")] string format = "json",
        [Description("Maximum rows to export. Default 10000.")] int maxRows = 10000,
        CancellationToken ct = default)
    {
        var adapter = _cm.GetAdapter(alias);
        if (adapter is null)
            return $"Error: Connection '{alias}' not found.";

        var trimmed = sql.Trim();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
        {
            return "Error: Only SELECT/WITH queries can be exported.";
        }

        format = format.ToLowerInvariant();
        if (format is not "json" and not "csv")
            return "Error: format must be 'json' or 'csv'.";

        try
        {
            var result = await adapter.QueryAsync(sql, maxRows, ct);

            if (result.Rows.Count == 0)
                return "Query returned no rows. Nothing to export.";

            // 解析路径：相对路径 → 工作区根目录
            var resolvedPath = ResolveExportPath(filePath);
            var dir = Path.GetDirectoryName(resolvedPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (format == "json")
            {
                var json = JsonSerializer.Serialize(result.Rows, JsonOptions);
                await File.WriteAllTextAsync(resolvedPath, json, ct);
            }
            else // csv
            {
                await using var writer = new StreamWriter(resolvedPath);
                // Header
                var columns = ((Dictionary<string, object?>)result.Rows[0]).Keys.ToList();
                await writer.WriteLineAsync(string.Join(",", columns.Select(EscapeCsv)));

                // Data rows
                foreach (var row in result.Rows)
                {
                    var dict = (Dictionary<string, object?>)row;
                    var values = columns.Select(c =>
                        dict.TryGetValue(c, out var v) ? EscapeCsv(v?.ToString() ?? "") : "");
                    await writer.WriteLineAsync(string.Join(",", values));
                }
            }

            var truncatedMsg = result.Truncated ? $" (truncated from {result.RowCount}+ rows)" : "";
            var baseInfo = GetExportBaseDir() != Directory.GetCurrentDirectory()
                ? $" (base: {GetExportBaseDir()})" : "";
            return $"Exported {result.RowCount} rows{truncatedMsg} to {Path.GetFullPath(resolvedPath)} in {format} format{baseInfo}. Elapsed: {result.Elapsed.TotalSeconds:F2}s";
        }
        catch (Exception ex)
        {
            return $"Export error: {ex.Message}";
        }
    }

    /// <summary>CSV 转义：含逗号/引号/换行的值用双引号包裹</summary>
    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    // ================================================================
    // Phase 5.4: 自动发现表关系
    // ================================================================

    [McpServerTool(Name = "db_find_relations"),
     Description("Discover all foreign key relationships between tables in the database. Returns a map of table → referenced tables. Useful for understanding the database schema and generating ER diagrams.")]
    public async Task<string> DbFindRelations(
        [Description("Connection alias")] string alias,
        CancellationToken ct = default)
    {
        var adapter = _cm.GetAdapter(alias);
        if (adapter is null)
            return $"Error: Connection '{alias}' not found.";

        try
        {
            var tables = await adapter.ListTablesAsync(ct);
            if (tables.Count == 0)
                return "No tables found in the database.";

            var allRelations = new List<object>();

            foreach (var table in tables)
            {
                try
                {
                    var schema = await adapter.DescribeTableAsync(table.Name, ct);
                    if (schema.ForeignKeys.Count > 0)
                    {
                        foreach (var fk in schema.ForeignKeys)
                        {
                            allRelations.Add(new
                            {
                                fromTable = schema.TableName,
                                fromColumn = fk.ColumnName,
                                toTable = fk.ReferencedTable,
                                toColumn = fk.ReferencedColumn,
                                constraintName = fk.Name
                            });
                        }
                    }
                }
                catch
                {
                    // Skip tables that can't be described (e.g., system tables)
                }
            }

            if (allRelations.Count == 0)
                return "No foreign key relationships found in the database.";

            // Build readable summary
            var grouped = allRelations
                .GroupBy(r => ((dynamic)r).fromTable)
                .OrderBy(g => g.Key);

            var lines = new List<string>
            {
                $"Foreign Key Relationships ({allRelations.Count} total):",
                new string('-', 70),
            };

            foreach (var group in grouped)
            {
                lines.Add($"📦 {group.Key}:");
                foreach (dynamic rel in group)
                {
                    lines.Add($"    {rel.fromColumn} → {rel.toTable}.{rel.toColumn}");
                }
            }

            lines.Add(new string('-', 70));

            // Also return JSON for programmatic use
            lines.Add("\nJSON (machine-readable):");
            lines.Add(JsonSerializer.Serialize(allRelations, JsonOptions));

            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error finding relations: {ex.Message}";
        }
    }
}
