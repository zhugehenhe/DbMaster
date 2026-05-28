# DbMaster — 多数据库 MCP 工具

让 AI 直接操作 SQLite / MySQL / PostgreSQL / SQL Server 的 MCP Server，支持 SSH 隧道远程连接。

## 快速开始

### 1. 发布为单文件
```bash
dotnet publish src/DbMaster.Stdio -c Release -o publish \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true --self-contained
```

### 2. 安装到 PATH
```powershell
# 创建工具目录并复制
mkdir $env:USERPROFILE\.dbmaster -Force
copy publish\DbMaster.Stdio.exe $env:USERPROFILE\.dbmaster\dbmaster.exe

# 添加到用户 PATH（重启终端生效）
[Environment]::SetEnvironmentVariable(
    "Path",
    "$env:USERPROFILE\.dbmaster;" + [Environment]::GetEnvironmentVariable("Path", "User"),
    "User"
)
```

### 3. 配置 VS Code（`%APPDATA%/Code/User/mcp.json`）
```json
{
  "servers": {
    "DbMaster": {
      "type": "stdio",
      "command": "dbmaster",
      "cwd": "${workspaceFolder}",
      "env": {
        "DBMASTER_EXPORT_DIR": "${workspaceFolder}"
      }
    }
  }
}
```

| 字段 | 作用 |
|------|------|
| `command` | 可执行文件（已安装到 PATH） |
| `cwd` | 工作目录 = 项目根目录 |
| `env.DBMASTER_EXPORT_DIR` | 导出文件默认保存位置 |

### 4. Reload VS Code Window，AI 即可使用

---

## 📋 工具目录（21 个工具）

### 🔌 连接管理
| 工具 | 说明 |
|------|------|
| `db_list_supported_types` | **工具目录** — 第一步调用，查看所有可用工具和数据库类型 |
| `db_connect` | 连接数据库，支持 auto 自动检测类型 |
| `db_disconnect` | 断开连接 |
| `db_list_connections` | 查看所有活动连接 |

### 📊 数据查询
| 工具 | 说明 |
|------|------|
| `db_execute_query` | 执行 SELECT 查询，返回 JSON |
| `db_explain_query` | EXPLAIN 分析执行计划，查慢查询 |
| `db_table_stats` | 统计所有表行数 |

### 🔍 表结构
| 工具 | 说明 |
|------|------|
| `db_list_tables` | 列出所有表 |
| `db_describe_table` | 查看表结构（列/类型/主键/外键/索引/DDL） |
| `db_find_relations` | 发现所有 FK 关系 |

### 📦 导出备份
| 工具 | 说明 |
|------|------|
| `db_export_data` | 导出查询结果为 JSON/CSV |
| `db_export_schema` | 导出 DDL 建表语句为 .sql |
| `db_backup` | 全库备份（DDL + INSERT）为 .sql |

### 🔧 高级
| 工具 | 说明 |
|------|------|
| `db_generate_erd` | 生成 Mermaid ER 图 |
| `db_compare_schemas` | 对比两个表 schema 差异 |
| `db_save_profile` | 保存连接配置 |
| `db_load_profile` | 加载连接配置 |

### 🔐 SSH 隧道
| 工具 | 说明 |
|------|------|
| `db_ssh_tunnel` | 建立 SSH 端口转发（支持密码和密钥认证） |
| `db_ssh_disconnect` | 关闭隧道 |
| `db_ssh_list` | 列出活动隧道 |

---

## 🏗 项目结构

```
DbMaster/
├── src/
│   ├── DbMaster.Core/         # 接口、模型、连接管理器、适配器工厂、SSH隧道
│   ├── DbMaster.Adapters/     # SQLite/MySQL/PostgreSQL/SQL Server 适配器
│   ├── DbMaster.Server/       # HTTP MCP Server
│   ├── DbMaster.Stdio/        # Stdio MCP Server（VS Code 自动启动）
│   └── DbMaster.Client/       # 端到端测试客户端
├── tests/                     # 27 个单元测试 + 集成测试
├── docs/DESIGN.md             # 详细设计文档
└── publish/                   # 发布产物（.gitignore）
```

## 🔧 技术栈

- .NET 8.0 + ModelContextProtocol v1.3.0
- SSH.NET 2024.2.0（SSH 隧道）
- Npgsql / MySqlConnector / Microsoft.Data.Sqlite / Microsoft.Data.SqlClient
- xUnit 2.9.x（测试）

## 📊 项目状态

| 指标 | 值 |
|------|-----|
| MCP 工具 | 21 |
| 数据库适配器 | 4 |
| 测试 | 27/27 ✅ |
| 编译 | 6/6 ✅ 0 warnings |

## 🐛 已修复问题

| # | 问题 | 修复 |
|---|------|------|
| 1 | 并发连接竞态 | GetOrAdd + sentinel |
| 2 | 表名注入 | ValidateTableName 白名单 |
| 7 | PG 大小写 | pg_class.relname 替代 ::regclass |
| 8 | 中文乱码 | UnsafeRelaxedJsonEscaping |
| 13 | 显式 dbType 误判 | RegisterFactory 直接工厂 |

详见 [DESIGN.md](docs/DESIGN.md)
