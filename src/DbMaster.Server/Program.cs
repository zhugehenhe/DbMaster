using DbMaster.Core;
using DbMaster.Server.Tools;
using System.Runtime.CompilerServices;

// ⭐ 强制触发所有适配器静态构造函数 → AdapterFactory.Register()
RuntimeHelpers.RunClassConstructor(typeof(DbMaster.Adapters.SqliteAdapter).TypeHandle);
RuntimeHelpers.RunClassConstructor(typeof(DbMaster.Adapters.MySqlAdapter).TypeHandle);
RuntimeHelpers.RunClassConstructor(typeof(DbMaster.Adapters.PostgreSqlAdapter).TypeHandle);
RuntimeHelpers.RunClassConstructor(typeof(DbMaster.Adapters.SqlServerAdapter).TypeHandle);

var builder = WebApplication.CreateBuilder(args);

// ============================================================
// 注册核心服务
// ============================================================
builder.Services.AddSingleton<ConnectionManager>(_ => new ConnectionManager
{
    MaxConnections = 10,
    IdleTimeout = TimeSpan.FromMinutes(30),
});
builder.Services.AddSingleton<SshTunnelManager>();

// ============================================================
// MCP Server 配置
// ============================================================
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
    {
        Name = "DbMaster",
        Version = "1.0.0",
        Title = "DbMaster — 多数据库 MCP 工具",
        Description = "支持 SQLite/MySQL/PostgreSQL/SQL Server 的统一数据库管理 MCP Server",
    };
    options.Capabilities = new()
    {
        Tools = new(),
    };
})
    .WithHttpTransport(options =>
    {
        options.Stateless = false;
    })
    .WithTools<DatabaseTools>()
    .WithTools<SshTools>();

var app = builder.Build();

// 映射 MCP 端点（⚠️ 显式路径，v1.3.0 默认是 /）
app.MapMcp("/mcp");

app.Run();
