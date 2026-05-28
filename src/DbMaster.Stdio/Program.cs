using DbMaster.Core;
using DbMaster.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

// ============================================================
// DbMaster Stdio Server — VS Code 自动启动模式
// ⚠️ stdout = JSON-RPC，日志必须完全禁用
// ============================================================

// ⭐ 强制触发所有适配器静态构造函数 → AdapterFactory.Register()
RuntimeHelpers.RunClassConstructor(typeof(DbMaster.Adapters.SqliteAdapter).TypeHandle);
RuntimeHelpers.RunClassConstructor(typeof(DbMaster.Adapters.MySqlAdapter).TypeHandle);
RuntimeHelpers.RunClassConstructor(typeof(DbMaster.Adapters.PostgreSqlAdapter).TypeHandle);
RuntimeHelpers.RunClassConstructor(typeof(DbMaster.Adapters.SqlServerAdapter).TypeHandle);

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.None);

builder.Services.AddSingleton<ConnectionManager>(_ => new ConnectionManager
{
    MaxConnections = 10,
    IdleTimeout = TimeSpan.FromMinutes(30),
});
builder.Services.AddSingleton<SshTunnelManager>();

builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
    {
        Name = "DbMaster",
        Version = "1.0.0",
        Title = "DbMaster — 多数据库 MCP 工具",
        Description = "支持 SQLite/MySQL/PostgreSQL/SQL Server。db_list_supported_types 查看可用类型。",
    };
    options.Capabilities = new() { Tools = new() };
})
    .WithStdioServerTransport()
    .WithTools<DatabaseTools>()
    .WithTools<SshTools>();

await builder.Build().RunAsync();
