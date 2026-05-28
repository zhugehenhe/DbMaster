using System.Data.Common;
using System.Diagnostics;
using DbMaster.Core;

namespace DbMaster.Adapters;

/// <summary>
/// 适配器基类 — 提取公共的 QueryAsync 实现。
/// 子类只需提供 CreateConnection 和数据库特定的元数据查询。
/// </summary>
public abstract class BaseDbAdapter : IDbAdapter
{
    protected readonly string ConnectionString;

    protected BaseDbAdapter(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public abstract string DbType { get; }

    /// <summary>子类实现：创建数据库连接</summary>
    protected abstract DbConnection CreateConnection();

    /// <summary>子类实现：列出所有用户表</summary>
    protected abstract Task<List<TableInfo>> QueryTablesAsync(DbConnection conn, CancellationToken ct);

    /// <summary>子类实现：获取列信息</summary>
    protected abstract Task<List<ColumnInfo>> QueryColumnsAsync(DbConnection conn, string tableName, CancellationToken ct);

    /// <summary>子类实现：获取主键列名</summary>
    protected abstract Task<List<string>> QueryPrimaryKeysAsync(DbConnection conn, string tableName, CancellationToken ct);

    /// <summary>子类实现：获取外键</summary>
    protected abstract Task<List<ForeignKeyInfo>> QueryForeignKeysAsync(DbConnection conn, string tableName, CancellationToken ct);

    /// <summary>子类实现：获取索引</summary>
    protected abstract Task<List<IndexInfo>> QueryIndexesAsync(DbConnection conn, string tableName, CancellationToken ct);

    /// <summary>子类实现：获取建表SQL（可选）</summary>
    protected virtual Task<string?> QueryCreateSqlAsync(DbConnection conn, string tableName, CancellationToken ct)
        => Task.FromResult<string?>(null);

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        return true;
    }

    public async Task<QueryResult> QueryAsync(string sql, int maxRows, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var rows = new List<Dictionary<string, object?>>();
        var truncated = false;

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;

        while (await reader.ReadAsync(ct))
        {
            if (count >= maxRows) { truncated = true; break; }
            var row = new Dictionary<string, object?>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
            count++;
        }

        return new QueryResult { RowCount = rows.Count, Truncated = truncated, Rows = rows, Elapsed = sw.Elapsed };
    }

    public async Task<int> ExecuteAsync(string sql, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        return await QueryTablesAsync(conn, ct);
    }

    public async Task<TableSchema> DescribeTableAsync(string tableName, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        return new TableSchema
        {
            TableName = tableName,
            Columns = await QueryColumnsAsync(conn, tableName, ct),
            PrimaryKeys = await QueryPrimaryKeysAsync(conn, tableName, ct),
            ForeignKeys = await QueryForeignKeysAsync(conn, tableName, ct),
            Indexes = await QueryIndexesAsync(conn, tableName, ct),
            CreateSql = await QueryCreateSqlAsync(conn, tableName, ct),
        };
    }

    public virtual void Dispose() { }
}
