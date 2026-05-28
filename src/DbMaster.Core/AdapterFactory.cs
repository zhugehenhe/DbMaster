using System.Data.Common;

namespace DbMaster.Core;

/// <summary>
/// 适配器工厂 — 根据连接字符串自动识别数据库类型并创建对应的 IDbAdapter。
/// </summary>
public static class AdapterFactory
{
    /// <summary>
    /// 解析连接字符串并创建适配器。
    /// 启发式规则：检查关键字顺序为 SQLite → SQL Server → PostgreSQL → MySQL
    /// </summary>
    public static IDbAdapter Create(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // 使用 DbConnectionStringBuilder 解析（不验证，仅提取键值）
        var cs = new DbConnectionStringBuilder();
        try { cs.ConnectionString = connectionString; } catch { }

        // 检测关键字判断数据库类型
        if (HasKeyword(cs, "Data Source") && !HasKeyword(cs, "Server") && !HasKeyword(cs, "Host"))
        {
            // SQLite: Data Source=xxx.db（只有 Data Source，没有 Server/Host）
            return CreateSqliteAdapter(connectionString);
        }

        if (HasKeyword(cs, "TrustServerCertificate") || HasKeyword(cs, "Integrated Security"))
        {
            // SQL Server 特征
            return CreateSqlServerAdapter(connectionString);
        }

        if (HasKeyword(cs, "Host"))
        {
            // PostgreSQL: Host=xxx
            return CreatePostgreSqlAdapter(connectionString);
        }

        // 默认 MySQL
        return CreateMySqlAdapter(connectionString);
    }

    private static bool HasKeyword(DbConnectionStringBuilder cs, string key)
        => cs.Keys.Cast<string>().Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));

    // 工厂方法 — 使用反射创建适配器避免 DbMaster.Core 依赖 DbMaster.Adapters
    // 实际实现放在 Adapters 项目中

    private static IDbAdapter CreateSqliteAdapter(string cs)
    {
        var type = Type.GetType("DbMaster.Adapters.SqliteAdapter, DbMaster.Adapters")
            ?? throw new InvalidOperationException("SQLite adapter not found. Ensure DbMaster.Adapters is referenced.");
        return (IDbAdapter)Activator.CreateInstance(type, cs)!;
    }

    private static IDbAdapter CreateMySqlAdapter(string cs)
    {
        var type = Type.GetType("DbMaster.Adapters.MySqlAdapter, DbMaster.Adapters")
            ?? throw new InvalidOperationException("MySQL adapter not found.");
        return (IDbAdapter)Activator.CreateInstance(type, cs)!;
    }

    private static IDbAdapter CreatePostgreSqlAdapter(string cs)
    {
        var type = Type.GetType("DbMaster.Adapters.PostgreSqlAdapter, DbMaster.Adapters")
            ?? throw new InvalidOperationException("PostgreSQL adapter not found.");
        return (IDbAdapter)Activator.CreateInstance(type, cs)!;
    }

    private static IDbAdapter CreateSqlServerAdapter(string cs)
    {
        var type = Type.GetType("DbMaster.Adapters.SqlServerAdapter, DbMaster.Adapters")
            ?? throw new InvalidOperationException("SQL Server adapter not found.");
        return (IDbAdapter)Activator.CreateInstance(type, cs)!;
    }
}
