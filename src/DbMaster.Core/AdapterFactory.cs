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
    /// 解析连接字符串并创建匹配的 IDbAdapter。
    /// 按注册顺序依次尝试检测器，返回第一个匹配的适配器。
    /// </summary>
    /// <exception cref="InvalidOperationException">无法识别的数据库类型</exception>
    public static IDbAdapter Create(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // 基础验证：连接串至少包含一个键值对
        try
        {
            var test = new DbConnectionStringBuilder { ConnectionString = connectionString };
            if (test.Keys.Count == 0)
                throw new ArgumentException("Connection string contains no key-value pairs.");
        }
        catch (ArgumentException)
        {
            throw; // 格式错误，直接向上抛出
        }

        // 依次尝试已注册的检测器
        foreach (var detector in _detectors)
        {
            var adapter = detector(connectionString);
            if (adapter is not null)
                return adapter;
        }

        throw new InvalidOperationException(
            "Unable to detect database type from connection string. " +
            "Supported types: SQLite, MySQL, PostgreSQL, SQL Server. " +
            "Ensure the appropriate adapter package is installed and registered.");
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
