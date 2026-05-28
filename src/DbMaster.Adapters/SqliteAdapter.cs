using System.Data;
using System.Diagnostics;
using DbMaster.Core;
using Microsoft.Data.Sqlite;

namespace DbMaster.Adapters;

/// <summary>
/// SQLite 数据库适配器 — 实现 IDbAdapter 接口。
/// 支持内存数据库（Data Source=:memory:）和文件数据库。
/// 
/// ⭐ 静态构造函数中自动注册到 AdapterFactory，无需额外配置。
/// </summary>
public sealed class SqliteAdapter : IDbAdapter
{
    private readonly string _connectionString;

    static SqliteAdapter()
    {
        // ⭐ 自动注册：应用启动时执行一次
        AdapterFactory.Register(cs =>
        {
            // SQLite 特征：只有 Data Source，没有 Server 或 Host
            if (AdapterFactory.HasKeyword(cs, "Data Source") &&
                !AdapterFactory.HasKeyword(cs, "Server") &&
                !AdapterFactory.HasKeyword(cs, "Host"))
            {
                return new SqliteAdapter(cs);
            }
            return null;
        });
    }

    public SqliteAdapter(string connectionString)
    {
        _connectionString = connectionString;
    }

    public string DbType => "sqlite";

    /// <summary>创建并打开连接，测试可用性</summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return true;
    }

    /// <summary>执行只读查询</summary>
    public async Task<QueryResult> QueryAsync(string sql, int maxRows, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var rows = new List<Dictionary<string, object?>>();
        var truncated = false;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            if (count >= maxRows)
            {
                truncated = true;
                break;
            }

            var row = new Dictionary<string, object?>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);

            rows.Add(row);
            count++;
        }

        return new QueryResult
        {
            RowCount = rows.Count,
            Truncated = truncated,
            Rows = rows,
            Elapsed = sw.Elapsed,
        };
    }

    /// <summary>执行写操作（INSERT/UPDATE/DELETE/DDL）</summary>
    public async Task<int> ExecuteAsync(string sql, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>列出所有用户表</summary>
    public async Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken ct = default)
    {
        var tables = new List<TableInfo>();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var tableName = reader.GetString(0);

            // 获取行数
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\"";
            var rowCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));

            tables.Add(new TableInfo
            {
                Name = tableName,
                RowCount = rowCount,
            });
        }

        return tables;
    }

    /// <summary>获取表结构详情</summary>
    public async Task<TableSchema> DescribeTableAsync(string tableName, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var schema = new TableSchema { TableName = tableName };

        // PRAGMA table_info — 列信息
        await using var colCmd = conn.CreateCommand();
        colCmd.CommandText = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"")}\")";

        var columns = new List<ColumnInfo>();
        var pkColumns = new List<string>();

        await using var colReader = await colCmd.ExecuteReaderAsync(ct);
        while (await colReader.ReadAsync(ct))
        {
            var col = new ColumnInfo
            {
                Name = colReader.GetString(1),
                DataType = !colReader.IsDBNull(2) ? colReader.GetString(2) : "TEXT",
                IsNullable = colReader.GetInt32(3) == 0, // 0=false(not null), 1=true(nullable)
                IsPrimaryKey = colReader.GetInt32(5) != 0,
                DefaultValue = !colReader.IsDBNull(4) ? colReader.GetString(4) : null,
            };
            columns.Add(col);

            if (col.IsPrimaryKey)
                pkColumns.Add(col.Name);
        }
        schema.Columns = columns;
        schema.PrimaryKeys = pkColumns;

        // PRAGMA foreign_key_list — 外键
        await using var fkCmd = conn.CreateCommand();
        fkCmd.CommandText = $"PRAGMA foreign_key_list(\"{tableName.Replace("\"", "\"\"")}\")";

        var fks = new List<ForeignKeyInfo>();
        await using var fkReader = await fkCmd.ExecuteReaderAsync(ct);
        while (await fkReader.ReadAsync(ct))
        {
            fks.Add(new ForeignKeyInfo
            {
                ColumnName = fkReader.GetString(3),
                ReferencedTable = fkReader.GetString(2),
                ReferencedColumn = fkReader.GetString(4),
            });
        }
        schema.ForeignKeys = fks;

        // PRAGMA index_list — 索引
        await using var idxCmd = conn.CreateCommand();
        idxCmd.CommandText = $"PRAGMA index_list(\"{tableName.Replace("\"", "\"\"")}\")";

        var indexes = new List<IndexInfo>();
        await using var idxReader = await idxCmd.ExecuteReaderAsync(ct);
        while (await idxReader.ReadAsync(ct))
        {
            var idxName = idxReader.GetString(1);
            var isUnique = idxReader.GetInt32(2) != 0;

            // 获取索引列
            await using var idxColCmd = conn.CreateCommand();
            idxColCmd.CommandText = $"PRAGMA index_info(\"{idxName.Replace("\"", "\"\"")}\")";
            var idxCols = new List<string>();
            await using var idxColReader = await idxColCmd.ExecuteReaderAsync(ct);
            while (await idxColReader.ReadAsync(ct))
                idxCols.Add(idxColReader.GetString(2));

            indexes.Add(new IndexInfo { Name = idxName, Columns = idxCols, IsUnique = isUnique });
        }
        schema.Indexes = indexes;

        // 获取建表 SQL
        await using var sqlCmd = conn.CreateCommand();
        sqlCmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=@name";
        sqlCmd.Parameters.AddWithValue("@name", tableName);
        var createSql = await sqlCmd.ExecuteScalarAsync(ct) as string;
        schema.CreateSql = createSql;

        return schema;
    }

    public void Dispose()
    {
        // SQLite 连接由每次方法内部创建和释放，无需持有长连接
    }
}
