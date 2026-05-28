using System.Data.Common;
using System.Diagnostics;
using DbMaster.Core;

namespace DbMaster.Adapters;

/// <summary>
/// 适配器基类 — 提取公共的 QueryAsync 实现 + 连接池优化（Phase 5.1）。
/// 子类只需提供 CreateConnection 和数据库特定的元数据查询。
/// </summary>
public abstract class BaseDbAdapter : IDbAdapter
{
    protected readonly string ConnectionString;

    private DbConnection? _connection;
    private readonly SemaphoreSlim _connLock = new(1, 1);

    // Phase 7.3: 表元数据缓存
    private IReadOnlyList<TableInfo>? _cachedTables;
    private DateTime _cacheTime = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    protected BaseDbAdapter(string connectionString)
    {
        ConnectionString = connectionString;
    }

    /// <summary>修复 #4：ToString 不暴露连接字符串中的密码</summary>
    public override string ToString() => $"{DbType} adapter (connected)";

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

    /// <summary>子类实现：EXPLAIN 语法前缀（可选，默认 EXPLAIN）</summary>
    protected virtual string ExplainPrefix(string sql) => $"EXPLAIN {sql}";

    /// <summary>验证表名仅包含安全字符</summary>
    protected static void ValidateTableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            throw new ArgumentException("Invalid table name.");

        // 修复 #2：仅允许 alphanumeric + underscore + hyphen
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9_\-]+$"))
            throw new ArgumentException(
                "Table name contains invalid characters. Only letters, numbers, underscores, and hyphens allowed.");
    }

    // ================================================================
    // Phase 5.1: 连接池 — 单连接复用，消除每次查询重建开销
    // ================================================================

    /// <summary>获取或创建复用的数据库连接（线程安全）</summary>
    protected async Task<DbConnection> GetConnectionAsync(CancellationToken ct = default)
    {
        if (_connection is not null)
            return _connection;

        await _connLock.WaitAsync(ct);
        try
        {
            if (_connection is null)
            {
                _connection = CreateConnection();
                await _connection.OpenAsync(ct);
            }
            return _connection;
        }
        finally
        {
            _connLock.Release();
        }
    }

    /// <summary>重置连接（连接断开后重建）</summary>
    protected async Task<DbConnection> ResetConnectionAsync(CancellationToken ct = default)
    {
        await _connLock.WaitAsync(ct);
        try
        {
            if (_connection is not null)
            {
                try { await _connection.CloseAsync(); } catch { /* ignore */ }
                try { await _connection.DisposeAsync(); } catch { /* ignore */ }
                _connection = null;
            }
            _connection = CreateConnection();
            await _connection.OpenAsync(ct);
            return _connection;
        }
        finally
        {
            _connLock.Release();
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        var conn = CreateConnection();
        try
        {
            await conn.OpenAsync(ct);
            return true;
        }
        finally
        {
            await conn.CloseAsync();
            await conn.DisposeAsync();
        }
    }

    // ================================================================
    // 核心方法（使用复用连接）
    // ================================================================

    public async Task<QueryResult> QueryAsync(string sql, int maxRows, CancellationToken ct = default)
    {
        return await QueryAsync(sql, maxRows, 30, ct);
    }

    /// <summary>Phase 7.2: 带超时的查询</summary>
    public async Task<QueryResult> QueryAsync(string sql, int maxRows, int timeoutSeconds, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var rows = new List<Dictionary<string, object?>>();
        var truncated = false;

        var conn = await GetConnectionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = Math.Max(1, timeoutSeconds);

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
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken ct = default)
    {
        // Phase 7.3: 元数据缓存（30s TTL）
        if (_cachedTables is not null && (DateTime.UtcNow - _cacheTime) < CacheTtl)
            return _cachedTables;

        var conn = await GetConnectionAsync(ct);
        _cachedTables = await QueryTablesAsync(conn, ct);
        _cacheTime = DateTime.UtcNow;
        return _cachedTables;
    }

    public async Task<TableSchema> DescribeTableAsync(string tableName, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);

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

    // ================================================================
    // Phase 5.2: EXPLAIN 分析
    // ================================================================

    /// <summary>执行 EXPLAIN 查询，返回执行计划</summary>
    public async Task<QueryResult> ExplainAsync(string sql, int maxRows, CancellationToken ct = default)
    {
        var explainSql = ExplainPrefix(sql);
        return await QueryAsync(explainSql, maxRows, ct);
    }

    public virtual void Dispose()
    {
        _connLock.Wait();
        try
        {
            _connection?.Close();
            _connection?.Dispose();
            _connection = null;
        }
        finally
        {
            _connLock.Release();
            _connLock.Dispose();
        }
    }
}
