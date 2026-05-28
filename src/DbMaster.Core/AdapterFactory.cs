using System.Data.Common;

namespace DbMaster.Core;

/// <summary>
/// 适配器工厂 — 通过委托注册模式解耦 Core 和 Adapters 项目。
/// 各适配器项目在初始化时调用 <see cref="Register"/> 注册检测器，
/// 无需反射即可动态创建适配器。
/// </summary>
public static class AdapterFactory
{
    private static readonly List<AdapterDetector> _detectors = [];
    private static readonly Dictionary<string, Func<string, IDbAdapter>> _factories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 适配器检测器委托：接受连接字符串，返回适配器实例或 null（表示不匹配）。
    /// </summary>
    public delegate IDbAdapter? AdapterDetector(string connectionString);

    /// <summary>
    /// 注册一个适配器检测器。通常在 Adapters 项目的静态构造函数中调用。
    /// </summary>
    public static void Register(AdapterDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _detectors.Add(detector);
    }

    /// <summary>
    /// 注册一个直接工厂（修复 #13：显式 dbType 跳过关键字检测）。
    /// 通常在 Adapters 项目的静态构造函数中，与 Register 一起调用。
    /// </summary>
    public static void RegisterFactory(string dbType, Func<string, IDbAdapter> factory)
    {
        _factories[dbType.ToLowerInvariant()] = factory;
    }

    /// <summary>
    /// 解析连接字符串并创建匹配的 IDbAdapter。
    /// </summary>
    /// <param name="connectionString">数据库连接字符串</param>
    /// <param name="dbType">
    /// 数据库类型，null 或 "auto" 表示自动检测。
    /// 显式值： "sqlite", "mysql", "postgresql", "sqlserver"
    /// </param>
    /// <exception cref="ArgumentException">无效的 dbType 值</exception>
    /// <exception cref="InvalidOperationException">无法识别数据库类型（auto 模式）</exception>
    public static IDbAdapter Create(string connectionString, string? dbType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // 基础验证
        try
        {
            var test = new DbConnectionStringBuilder { ConnectionString = connectionString };
            if (test.Keys.Count == 0)
                throw new ArgumentException("Connection string contains no key-value pairs.");
        }
        catch (ArgumentException)
        {
            throw;
        }

        // 显式 dbType → 优先用直接工厂（不检查连接串关键字），fallback 到检测器
        if (!string.IsNullOrEmpty(dbType) && dbType != "auto")
        {
            var normalized = dbType.ToLowerInvariant();

            // 修复 #13：优先用 RegisterFactory 注册的直接工厂
            if (_factories.TryGetValue(normalized, out var factory))
                return factory(connectionString);

            // fallback：遍历检测器
            foreach (var detector in _detectors)
            {
                var adapter = detector(connectionString);
                if (adapter is not null)
                {
                    if (adapter.DbType == normalized)
                        return adapter;
                    adapter.Dispose();
                }
            }
            throw new ArgumentException(
                $"Unknown or unregistered database type: '{dbType}'. " +
                "Supported types: sqlite, mysql, postgresql, sqlserver. " +
                "Use 'auto' for automatic detection.");
        }

        // auto 模式 → 依次尝试检测器
        foreach (var detector in _detectors)
        {
            var adapter = detector(connectionString);
            if (adapter is not null)
                return adapter;
        }

        throw new InvalidOperationException(
            "Unable to auto-detect database type. Try specifying dbType explicitly. " +
            "Supported types: sqlite, mysql, postgresql, sqlserver.");
    }

    /// <summary>
    /// 辅助方法：检查连接串中是否包含指定关键字。
    /// </summary>
    public static bool HasKeyword(string connectionString, string key)
    {
        try
        {
            var cs = new DbConnectionStringBuilder { ConnectionString = connectionString };
            return cs.Keys.Cast<string>().Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
