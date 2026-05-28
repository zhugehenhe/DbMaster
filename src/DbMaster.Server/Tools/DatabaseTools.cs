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

    // ================================================================
    // Phase 6.1: 生成 Mermaid ER 图
    // ================================================================

    [McpServerTool(Name = "db_generate_erd"),
     Description("Generate a Mermaid ER (Entity-Relationship) diagram from the database schema. Returns Mermaid syntax that renders as a visual diagram. Useful for understanding database structure at a glance.")]
    public async Task<string> DbGenerateErd(
        [Description("Connection alias")] string alias,
        [Description("Comma-separated table names to include, or leave empty for all tables")] string? tables = null,
        CancellationToken ct = default)
    {
        var adapter = _cm.GetAdapter(alias);
        if (adapter is null)
            return $"Error: Connection '{alias}' not found.";

        try
        {
            var allTables = await adapter.ListTablesAsync(ct);
            if (allTables.Count == 0)
                return "No tables found.";

            // Filter if specific tables requested
            var targetNames = tables?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var tablesToInclude = targetNames is { Length: > 0 }
                ? allTables.Where(t => targetNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList()
                : allTables.ToList();

            if (tablesToInclude.Count == 0)
                return $"None of the specified tables found. Available: {string.Join(", ", allTables.Select(t => t.Name))}";

            var mermaid = new List<string> { "```mermaid", "erDiagram" };
            var relations = new List<string>();

            foreach (var table in tablesToInclude)
            {
                try
                {
                    var schema = await adapter.DescribeTableAsync(table.Name, ct);

                    // Entity definition with columns
                    var columns = schema.Columns.Select(c =>
                    {
                        var type = c.DataType.Length > 20 ? c.DataType[..20] : c.DataType;
                        var markers = "";
                        if (c.IsPrimaryKey) markers += " PK";
                        return $"        {type} {c.Name}{markers}";
                    });

                    mermaid.Add($"    {schema.TableName} {{");
                    mermaid.AddRange(columns);
                    mermaid.Add("    }");
                    mermaid.Add("");

                    // Relationships
                    foreach (var fk in schema.ForeignKeys)
                    {
                        // Only include if both tables are in scope
                        if (tablesToInclude.Any(t =>
                            string.Equals(t.Name, fk.ReferencedTable, StringComparison.OrdinalIgnoreCase)))
                        {
                            relations.Add(
                                $"    {schema.TableName} ||--o{{ {fk.ReferencedTable} : \"{fk.ColumnName}\"");
                        }
                    }
                }
                catch
                {
                    mermaid.Add($"    {table.Name} {{");
                    mermaid.Add("        string _unknown_ \"(could not describe)\"");
                    mermaid.Add("    }");
                    mermaid.Add("");
                }
            }

            if (relations.Count > 0)
            {
                mermaid.AddRange(relations.Distinct());
            }

            mermaid.Add("```");
            mermaid.Add("");
            mermaid.Add($"Generated from {tablesToInclude.Count} table(s) with {relations.Distinct().Count()} relationship(s).");
            mermaid.Add("Paste into any Mermaid-compatible viewer (GitHub, Notion, VS Code Markdown Preview).");

            return string.Join("\n", mermaid);
        }
        catch (Exception ex)
        {
            return $"Error generating ERD: {ex.Message}";
        }
    }

    // ================================================================
    // Phase 6.2: Schema 对比
    // ================================================================

    [McpServerTool(Name = "db_compare_schemas"),
     Description("Compare the schemas of two tables (same alias or different aliases). Reports differences in columns, types, primary keys, foreign keys, and indexes. Useful for finding discrepancies between environments.")]
    public async Task<string> DbCompareSchemas(
        [Description("Connection alias for first table")] string alias1,
        [Description("Table name in first alias")] string tableName1,
        [Description("Connection alias for second table (can be same as alias1)")] string alias2,
        [Description("Table name in second alias")] string tableName2,
        CancellationToken ct = default)
    {
        var adapter1 = _cm.GetAdapter(alias1);
        if (adapter1 is null) return $"Error: Connection '{alias1}' not found.";
        var adapter2 = _cm.GetAdapter(alias2);
        if (adapter2 is null) return $"Error: Connection '{alias2}' not found.";

        try
        {
            var schema1 = await adapter1.DescribeTableAsync(tableName1, ct);
            var schema2 = await adapter2.DescribeTableAsync(tableName2, ct);

            var diffs = new List<string>
            {
                $"Schema Comparison: [{alias1}] {tableName1} vs [{alias2}] {tableName2}",
                new string('=', 70),
            };

            // Compare columns
            var cols1 = schema1.Columns.ToDictionary(c => c.Name, c => c);
            var cols2 = schema2.Columns.ToDictionary(c => c.Name, c => c);

            var allCols = cols1.Keys.Union(cols2.Keys).OrderBy(c => c).ToList();
            var colDiffs = new List<string>();

            foreach (var col in allCols)
            {
                var in1 = cols1.TryGetValue(col, out var c1);
                var in2 = cols2.TryGetValue(col, out var c2);

                if (in1 && !in2)
                    colDiffs.Add($"  ➕ {col}: only in [{alias1}]");
                else if (!in1 && in2)
                    colDiffs.Add($"  ➖ {col}: only in [{alias2}]");
                else if (c1!.DataType != c2!.DataType)
                    colDiffs.Add($"  🔄 {col}: type {c1.DataType} → {c2.DataType}");
                else if (c1.IsNullable != c2.IsNullable)
                    colDiffs.Add($"  🔄 {col}: nullable {c1.IsNullable} → {c2.IsNullable}");
                else if (c1.DefaultValue?.ToString() != c2.DefaultValue?.ToString())
                    colDiffs.Add($"  🔄 {col}: default {c1.DefaultValue} → {c2.DefaultValue}");
            }

            if (colDiffs.Count > 0)
            {
                diffs.Add($"\n📋 Column Differences ({colDiffs.Count}):");
                diffs.AddRange(colDiffs);
            }
            else
            {
                diffs.Add("\n📋 Columns: ✅ Identical");
            }

            // Compare primary keys
            var pk1 = schema1.PrimaryKeys.OrderBy(x => x).ToList();
            var pk2 = schema2.PrimaryKeys.OrderBy(x => x).ToList();
            if (!pk1.SequenceEqual(pk2))
                diffs.Add($"\n🔑 Primary Key: [{string.Join(",", pk1)}] vs [{string.Join(",", pk2)}]");
            else
                diffs.Add($"\n🔑 Primary Key: ✅ Identical ({string.Join(",", pk1)})");

            // Compare foreign keys
            var fk1Set = schema1.ForeignKeys.Select(f => $"{f.ColumnName}→{f.ReferencedTable}.{f.ReferencedColumn}").ToHashSet();
            var fk2Set = schema2.ForeignKeys.Select(f => $"{f.ColumnName}→{f.ReferencedTable}.{f.ReferencedColumn}").ToHashSet();
            var fkOnly1 = fk1Set.Except(fk2Set).ToList();
            var fkOnly2 = fk2Set.Except(fk1Set).ToList();

            if (fkOnly1.Count > 0 || fkOnly2.Count > 0)
            {
                diffs.Add($"\n🔗 Foreign Key Differences:");
                foreach (var fk in fkOnly1) diffs.Add($"  ➕ [{alias1}]: {fk}");
                foreach (var fk in fkOnly2) diffs.Add($"  ➖ [{alias2}]: {fk}");
            }
            else
            {
                diffs.Add($"\n🔗 Foreign Keys: ✅ Identical ({fk1Set.Count} total)");
            }

            // Compare indexes
            var idx1Set = schema1.Indexes.Select(i => $"{i.Name}({string.Join(",", i.Columns)}) {(i.IsUnique ? "U" : "")}").ToHashSet();
            var idx2Set = schema2.Indexes.Select(i => $"{i.Name}({string.Join(",", i.Columns)}) {(i.IsUnique ? "U" : "")}").ToHashSet();
            var idxOnly1 = idx1Set.Except(idx2Set).ToList();
            var idxOnly2 = idx2Set.Except(idx1Set).ToList();

            if (idxOnly1.Count > 0 || idxOnly2.Count > 0)
            {
                diffs.Add($"\n📑 Index Differences:");
                foreach (var idx in idxOnly1) diffs.Add($"  ➕ [{alias1}]: {idx}");
                foreach (var idx in idxOnly2) diffs.Add($"  ➖ [{alias2}]: {idx}");
            }
            else
            {
                diffs.Add($"\n📑 Indexes: ✅ Identical ({idx1Set.Count} total)");
            }

            // Summary
            var totalDiffs = colDiffs.Count + fkOnly1.Count + fkOnly2.Count + idxOnly1.Count + idxOnly2.Count;
            if (!pk1.SequenceEqual(pk2)) totalDiffs++;
            diffs.Add($"\n{new string('=', 70)}");
            diffs.Add(totalDiffs == 0
                ? "✅ Schemas are identical — no differences found."
                : $"⚠️  {totalDiffs} difference(s) found between the two schemas.");

            return string.Join("\n", diffs);
        }
        catch (Exception ex)
        {
            return $"Error comparing schemas: {ex.Message}";
        }
    }

    // ================================================================
    // Phase 6.3: 导出 DDL Schema
    // ================================================================

    [McpServerTool(Name = "db_export_schema"),
     Description("Export DDL (CREATE TABLE) statements for all tables to a SQL file. Safer alternative to full database backup — reconstructs the schema without data. Useful for version control, migration, or documentation.")]
    public async Task<string> DbExportSchema(
        [Description("Connection alias")] string alias,
        [Description("Output file path (e.g., 'schema.sql'). Relative paths resolve to workspace root.")] string filePath,
        [Description("Comma-separated table names, or empty for all")] string? tables = null,
        CancellationToken ct = default)
    {
        var adapter = _cm.GetAdapter(alias);
        if (adapter is null)
            return $"Error: Connection '{alias}' not found.";

        try
        {
            var allTables = await adapter.ListTablesAsync(ct);
            if (allTables.Count == 0)
                return "No tables found.";

            var targetNames = tables?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var tablesToExport = targetNames is { Length: > 0 }
                ? allTables.Where(t => targetNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList()
                : allTables.ToList();

            if (tablesToExport.Count == 0)
                return $"None of the specified tables found.";

            var resolvedPath = ResolveExportPath(filePath);
            var dir = Path.GetDirectoryName(resolvedPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var exportedCount = 0;
            await using var writer = new StreamWriter(resolvedPath);

            await writer.WriteLineAsync(
                $"-- DbMaster Schema Export: {alias} ({adapter.DbType})");
            await writer.WriteLineAsync(
                $"-- Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            await writer.WriteLineAsync(
                $"-- Tables: {tablesToExport.Count}\n");

            foreach (var table in tablesToExport)
            {
                try
                {
                    var schema = await adapter.DescribeTableAsync(table.Name, ct);

                    if (!string.IsNullOrEmpty(schema.CreateSql))
                    {
                        await writer.WriteLineAsync(
                            $"-- ============================================================");
                        await writer.WriteLineAsync($"-- Table: {schema.TableName}");
                        await writer.WriteLineAsync(
                            $"-- Columns: {schema.Columns.Count}, PK: {string.Join(",", schema.PrimaryKeys)}");
                        await writer.WriteLineAsync(
                            $"-- ============================================================");
                        await writer.WriteLineAsync(schema.CreateSql);
                        await writer.WriteLineAsync();
                        exportedCount++;
                    }
                    else
                    {
                        await writer.WriteLineAsync(
                            $"-- Table: {schema.TableName} (CREATE SQL not available for {adapter.DbType})");
                        await writer.WriteLineAsync();
                    }
                }
                catch (Exception ex)
                {
                    await writer.WriteLineAsync(
                        $"-- ERROR describing {table.Name}: {ex.Message}");
                }
            }

            await writer.WriteLineAsync(
                $"-- Exported {exportedCount}/{tablesToExport.Count} tables successfully.");

            return $"Exported {exportedCount}/{tablesToExport.Count} table DDLs to {Path.GetFullPath(resolvedPath)}. " +
                   $"Database type: {adapter.DbType}";
        }
        catch (Exception ex)
        {
            return $"Error exporting schema: {ex.Message}";
        }
    }
}
