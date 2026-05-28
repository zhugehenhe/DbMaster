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
     Description("【工具目录】查看所有支持的数据库类型和全部可用工具。第一步调用此工具了解能做什么。")]
    public static string DbListSupportedTypes()
    {
        return """
            📋 DbMaster 工具目录 — 支持 SQLite / MySQL / PostgreSQL / SQL Server

            🔌 连接管理:
              db_connect          连接数据库（支持 auto 自动检测类型）
              db_disconnect       断开连接
              db_list_connections 查看所有活动连接

            📊 数据查询:
              db_execute_query    执行 SELECT 查询 → 返回 JSON
              db_explain_query    分析 SQL 执行计划 → 查慢查询原因
              db_table_stats      统计所有表的行数

            🔍 表结构探索:
              db_list_tables      列出数据库中所有表
              db_describe_table   查看表结构（列、类型、主键、外键、索引、建表SQL）
              db_find_relations   发现所有表之间的外键关系

            📦 导出与备份:
              db_export_data      将查询结果导出为 JSON/CSV 文件
              db_export_schema    导出所有表的建表 DDL 为 .sql 文件
              db_backup           全库备份（DDL + INSERT 数据）为 .sql 文件

            🔧 高级工具:
              db_generate_erd     生成 Mermaid ER 图 → 可视化数据库结构
              db_compare_schemas  对比两个表的 schema 差异 → 迁移验证
              db_save_profile     保存连接配置到文件
              db_load_profile     加载已保存的连接配置

            🔐 SSH 隧道（远程数据库）:
              db_ssh_tunnel       建立 SSH 端口转发隧道 → 访问无公网IP的数据库
              db_ssh_disconnect   关闭指定隧道
              db_ssh_list         列出所有活动隧道

            💡 典型工作流:
              db_list_supported_types → db_connect → db_list_tables → db_describe_table → db_execute_query
            """;
    }

    // ================================================================
    // 连接管理
    // ================================================================

    [McpServerTool(Name = "db_connect"),
     Description("【连接数据库】建立数据库连接并设置别名 → 先调用 db_list_supported_types 查看类型")]
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

    [McpServerTool(Name = "db_disconnect"), Description("【断开连接】断开指定别名的数据库连接")]
    public string DbDisconnect(
        [Description("Connection alias to disconnect")] string alias)
    {
        return _cm.Disconnect(alias)
            ? $"Disconnected '{alias}'."
            : $"Alias '{alias}' not found.";
    }

    [McpServerTool(Name = "db_list_connections"), Description("【查看连接】列出所有活动连接及其状态")]
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
     Description("【执行查询】执行 SELECT 查询返回 JSON → 改/删数据用 db_execute_command")]
    public async Task<string> DbExecuteQuery(
        [Description("Connection alias")] string alias,
        [Description("SQL SELECT query to execute")] string sql,
        [Description("Maximum rows to return. Default 100.")] int maxRows = 100,
        [Description("Query timeout in seconds. Default 30, max 300.")] int timeoutSeconds = 30,
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
            var result = await adapter.QueryAsync(sql, maxRows, timeoutSeconds, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return $"Query error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "db_execute_command"),
     Description("【执行写操作】INSERT/UPDATE/DELETE/DDL → 需 confirm='CONFIRM'，DROP 需额外确认")]
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

    [McpServerTool(Name = "db_list_tables"), Description("【列出表】查看数据库中所有用户表及行数")]
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
     Description("【查看表结构】显示列/类型/主键/外键/索引/建表SQL → 理解一张表")]
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

    [McpServerTool(Name = "db_table_stats"), Description("【表统计】显示所有表的行数排行和汇总")]
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
     Description("【分析慢查询】用 EXPLAIN 分析 SQL 执行计划 → 查性能瓶颈/缺索引")]
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
     Description("【导出数据】查询结果导出为 JSON 或 CSV 文件 → 数据分析/分享")]
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
     Description("【发现关系】自动查找所有表之间的外键关联 → 理解数据模型")]
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
     Description("【生成ER图】生成 Mermaid ER 图语法 → 可粘贴到 GitHub/Notion 渲染")]
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
     Description("【对比Schema】比较两个表的列/类型/主键/外键/索引差异 → 迁移验证")]
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
     Description("【导出DDL】导出所有表的建表语句为 .sql 文件 → 版本控制/文档")]
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

    // ================================================================
    // db_backup: 全库备份（DDL + INSERT 数据）
    // ================================================================

    [McpServerTool(Name = "db_backup"),
     Description("【全库备份】导出 DDL + INSERT 数据为 .sql 文件 → 完整可恢复")]
    public async Task<string> DbBackup(
        [Description("Connection alias")] string alias,
        [Description("Output file path (e.g., 'backup.sql'). Relative paths resolve to workspace root.")] string filePath,
        [Description("Comma-separated table names to include, or empty for all tables")] string? tables = null,
        [Description("Maximum rows per table (0 = unlimited). Default 50000.")] int maxRowsPerTable = 50000,
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
            var tablesToBackup = targetNames is { Length: > 0 }
                ? allTables.Where(t => targetNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList()
                : allTables.ToList();

            if (tablesToBackup.Count == 0)
                return "None of the specified tables found.";

            var resolvedPath = ResolveExportPath(filePath);
            var dir = Path.GetDirectoryName(resolvedPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ddlCount = 0;
            var dataCount = 0;
            var totalRows = 0L;

            await using var writer = new StreamWriter(resolvedPath);

            await writer.WriteLineAsync("-- ============================================================");
            await writer.WriteLineAsync($"-- DbMaster Full Backup");
            await writer.WriteLineAsync($"-- Database: {alias} ({adapter.DbType})");
            await writer.WriteLineAsync($"-- Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            await writer.WriteLineAsync($"-- Tables: {tablesToBackup.Count}");
            await writer.WriteLineAsync("-- ============================================================");
            await writer.WriteLineAsync();

            foreach (var table in tablesToBackup)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var schema = await adapter.DescribeTableAsync(table.Name, ct);
                    await writer.WriteLineAsync("-- ============================================================");
                    await writer.WriteLineAsync($"-- Table: {schema.TableName}");
                    await writer.WriteLineAsync($"-- Columns: {schema.Columns.Count}, Rows: {table.RowCount:N0}");
                    await writer.WriteLineAsync("-- ============================================================");

                    // 1. DDL
                    if (!string.IsNullOrEmpty(schema.CreateSql))
                    {
                        await writer.WriteLineAsync(schema.CreateSql + ";");
                        await writer.WriteLineAsync();
                        ddlCount++;
                    }
                    else
                    {
                        await writer.WriteLineAsync(
                            $"-- (CREATE SQL not available for {adapter.DbType}, schema-only backup)");
                    }

                    // 2. Data as INSERT statements
                    if (maxRowsPerTable > 0)
                    {
                        var quoting = adapter.DbType switch
                        {
                            "postgresql" => "\"",
                            "mysql" => "`",
                            "sqlserver" => "\"",
                            _ => "\""
                        };

                        var data = await adapter.QueryAsync(
                            $"SELECT * FROM {quoting}{table.Name}{quoting} LIMIT {maxRowsPerTable}",
                            maxRowsPerTable, ct);

                        if (data.Rows.Count > 0)
                        {
                            var columns = ((Dictionary<string, object?>)data.Rows[0]).Keys.ToList();
                            var colList = string.Join(", ", columns.Select(c => $"{quoting}{c}{quoting}"));

                            foreach (var row in data.Rows)
                            {
                                var dict = (Dictionary<string, object?>)row;
                                var values = string.Join(", ", columns.Select(c => FormatSqlValue(dict[c], adapter.DbType)));
                                await writer.WriteLineAsync($"INSERT INTO {quoting}{table.Name}{quoting} ({colList}) VALUES ({values});");
                            }

                            totalRows += data.Rows.Count;
                            dataCount++;
                        }

                        await writer.WriteLineAsync();
                    }
                }
                catch (Exception ex)
                {
                    await writer.WriteLineAsync(
                        $"-- ERROR backing up {table.Name}: {ex.Message}");
                    await writer.WriteLineAsync();
                }
            }

            await writer.WriteLineAsync("-- ============================================================");
            await writer.WriteLineAsync(
                $"-- Backup complete: {ddlCount} DDLs, {dataCount} data tables, {totalRows:N0} total rows");
            await writer.WriteLineAsync(
                $"-- Elapsed: {sw.Elapsed.TotalSeconds:F1}s");

            return $"Backup complete: {ddlCount} DDLs + {dataCount} data tables ({totalRows:N0} rows) " +
                   $"→ {Path.GetFullPath(resolvedPath)} ({new FileInfo(resolvedPath).Length:N0} bytes). " +
                   $"Elapsed: {sw.Elapsed.TotalSeconds:F1}s";
        }
        catch (Exception ex)
        {
            return $"Backup error: {ex.Message}";
        }
    }

    // ================================================================
    // Phase 7.4: 连接配置管理
    // ================================================================

    /// <summary>Profile 文件存储结构</summary>
    private sealed class ConnectionProfile
    {
        public string Alias { get; set; } = "";
        public string ConnectionString { get; set; } = "";
        public string DbType { get; set; } = "auto";
        public string? Description { get; set; }
    }

    private sealed class ProfileStore
    {
        public List<ConnectionProfile> Profiles { get; set; } = [];
    }

    [McpServerTool(Name = "db_save_profile"),
     Description("【保存配置】保存当前连接信息到 JSON 文件 → 下次用 db_load_profile 恢复")]
    public async Task<string> DbSaveProfile(
        [Description("Connection alias (must be connected)")] string alias,
        [Description("Optional description for this connection")] string? description = null,
        [Description("Profile file path. Default: 'dbmaster_profiles.json' in workspace root.")]
        string filePath = "dbmaster_profiles.json",
        CancellationToken ct = default)
    {
        var adapter = _cm.GetAdapter(alias);
        if (adapter is null)
            return $"Error: Connection '{alias}' not found.";

        try
        {
            var resolvedPath = ResolveExportPath(filePath);

            // Load existing profiles
            var store = new ProfileStore();
            if (File.Exists(resolvedPath))
            {
                var existing = await File.ReadAllTextAsync(resolvedPath, ct);
                store = JsonSerializer.Deserialize<ProfileStore>(existing) ?? new ProfileStore();
            }

            // Can't store connection string (hidden by design), store adapter's type info
            var existingProfile = store.Profiles.FirstOrDefault(p =>
                string.Equals(p.Alias, alias, StringComparison.OrdinalIgnoreCase));
            if (existingProfile is not null)
                store.Profiles.Remove(existingProfile);

            store.Profiles.Add(new ConnectionProfile
            {
                Alias = alias,
                ConnectionString = adapter.ToString()!, // DbType info only (password-safe)
                DbType = adapter.DbType,
                Description = description,
            });

            var json = JsonSerializer.Serialize(store, JsonOptions);
            await File.WriteAllTextAsync(resolvedPath, json, ct);

            return $"Profile '{alias}' saved to {Path.GetFullPath(resolvedPath)} " +
                   $"(type: {adapter.DbType}, desc: {description ?? "none"}). " +
                   $"Note: Connection string is NOT stored for security. Reconnect with db_connect.";

        }
        catch (Exception ex)
        {
            return $"Save profile error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "db_load_profile"),
     Description("【加载配置】从 JSON 文件读取已保存的连接配置列表")]
    public string DbLoadProfile(
        [Description("Profile file path. Default: 'dbmaster_profiles.json' in workspace root.")]
        string filePath = "dbmaster_profiles.json")
    {
        var resolvedPath = ResolveExportPath(filePath);

        if (!File.Exists(resolvedPath))
            return $"No profile file found at {Path.GetFullPath(resolvedPath)}. " +
                   "Use db_save_profile to create one.";

        try
        {
            var json = File.ReadAllText(resolvedPath);
            var store = JsonSerializer.Deserialize<ProfileStore>(json);

            if (store?.Profiles.Count == 0)
                return "No profiles found in file.";

            var lines = new List<string>
            {
                $"Saved Profiles ({store!.Profiles.Count}):",
                new string('-', 50),
            };

            foreach (var p in store.Profiles)
            {
                var connected = _cm.GetAdapter(p.Alias) is not null ? "🟢" : "⚪";
                lines.Add(
                    $"  {connected} [{p.Alias}] {p.DbType} — {p.Description ?? "(no description)"}");
            }

            lines.Add(new string('-', 50));
            lines.Add("To reconnect: db_connect(alias=\"name\", connStr=\"...\", dbType=\"...\")");

            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Load profile error: {ex.Message}";
        }
    }

    /// <summary>格式化 SQL 值：NULL / 字符串引号转义 / 数字原样</summary>
    private static string FormatSqlValue(object? value, string dbType)
    {
        if (value is null || value == DBNull.Value)
            return "NULL";

        return value switch
        {
            string s => $"'{s.Replace("'", "''")}'",
            char c => $"'{c}'",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
            DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:sszzz}'",
            TimeSpan ts => $"'{ts}'",
            Guid g => $"'{g}'",
            bool b => dbType == "postgresql" ? b.ToString().ToUpperInvariant() : (b ? "1" : "0"),
            byte[] bytes => dbType == "postgresql"
                ? $"'\\x{Convert.ToHexString(bytes).ToLowerInvariant()}'"
                : $"X'{Convert.ToHexString(bytes)}'",
            _ => value.ToString() ?? "NULL"
        };
    }
}
