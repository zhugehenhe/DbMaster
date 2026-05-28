namespace DbMaster.Core;

/// <summary>
/// 查询结果，包含行数据、行数、耗时等元信息。
/// </summary>
public class QueryResult
{
    public int RowCount { get; set; }
    public bool Truncated { get; set; }
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; set; } = [];
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// 数据表基本信息。
/// </summary>
public class TableInfo
{
    public string Name { get; set; } = "";
    public string? Schema { get; set; }
    public long RowCount { get; set; }
    public string? Comment { get; set; }
}

/// <summary>
/// 列定义信息。
/// </summary>
public class ColumnInfo
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public bool IsNullable { get; set; }
    public bool IsPrimaryKey { get; set; }
    public string? DefaultValue { get; set; }
    public string? Comment { get; set; }
}

/// <summary>
/// 完整的表结构描述。
/// </summary>
public class TableSchema
{
    public string TableName { get; set; } = "";
    public IReadOnlyList<ColumnInfo> Columns { get; set; } = [];
    public IReadOnlyList<string> PrimaryKeys { get; set; } = [];
    public IReadOnlyList<ForeignKeyInfo> ForeignKeys { get; set; } = [];
    public IReadOnlyList<IndexInfo> Indexes { get; set; } = [];
    public string? CreateSql { get; set; }
}

/// <summary>
/// 外键关联信息。
/// </summary>
public class ForeignKeyInfo
{
    public string Name { get; set; } = "";
    public string ColumnName { get; set; } = "";
    public string ReferencedTable { get; set; } = "";
    public string ReferencedColumn { get; set; } = "";
}

/// <summary>
/// 索引信息。
/// </summary>
public class IndexInfo
{
    public string Name { get; set; } = "";
    public IReadOnlyList<string> Columns { get; set; } = [];
    public bool IsUnique { get; set; }
}

/// <summary>
/// 连接信息（对外暴露，隐藏连接字符串）。
/// </summary>
public class ConnectionInfo
{
    public string Alias { get; set; } = "";
    public string DbType { get; set; } = "";
    public DateTime ConnectedAt { get; set; }
    public DateTime LastAccess { get; set; }
}
