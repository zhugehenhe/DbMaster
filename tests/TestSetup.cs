using System.Runtime.CompilerServices;
using Xunit;

namespace DbMaster.Tests;

/// <summary>
/// 测试初始化 — 触发所有适配器的静态构造函数，
/// 确保 AdapterFactory 注册了完整的检测器链。
/// </summary>
public class AdapterRegistrationFixture
{
    public AdapterRegistrationFixture()
    {
        // typeof() 不触发静态构造，必须用 RuntimeHelpers.RunClassConstructor
        RuntimeHelpers.RunClassConstructor(typeof(DbMaster.Adapters.SqliteAdapter).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(DbMaster.Adapters.MySqlAdapter).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(DbMaster.Adapters.PostgreSqlAdapter).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(DbMaster.Adapters.SqlServerAdapter).TypeHandle);
    }
}

[CollectionDefinition("AdapterRegistration")]
public class AdapterRegistrationCollection : ICollectionFixture<AdapterRegistrationFixture>
{
}
