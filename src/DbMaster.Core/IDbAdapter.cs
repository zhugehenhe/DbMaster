namespace DbMaster.Core;

/// <summary>
/// 数据库适配器统一接口。
/// 所有数据库驱动（SQLite/MySQL/PostgreSQL/SQL Server）必须实现此接口。
/// </summary>
public interface IDbAdapter : IDisposable
{
    /// <summary>数据库类型标识，如 "sqlite", "mysql", "postgresql", "sqlserver"</summary>
    string DbType { get; }

    /// <summary>测试连接是否有效</summary>
    Task<bool> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>执行只读查询（SELECT/WITH/PRAGMA）</summary>
    Task<QueryResult> QueryAsync(string sql, int maxRows, CancellationToken ct = default);

    /// <summary>执行只读查询，指定超时秒数（Phase 7.2）</summary>
    Task<QueryResult> QueryAsync(string sql, int maxRows, int timeoutSeconds, CancellationToken ct = default);

    /// <summary>执行写操作（INSERT/UPDATE/DELETE/DDL）</summary>
    Task<int> ExecuteAsync(string sql, CancellationToken ct = default);

    /// <summary>列出所有用户表</summary>
    Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken ct = default);

    /// <summary>获取单表的完整结构信息</summary>
    Task<TableSchema> DescribeTableAsync(string tableName, CancellationToken ct = default);

    /// <summary>执行 EXPLAIN 查询，返回执行计划（Phase 5.2）</summary>
    Task<QueryResult> ExplainAsync(string sql, int maxRows, CancellationToken ct = default);
}
