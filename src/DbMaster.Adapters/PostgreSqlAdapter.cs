using System.Data.Common;
using DbMaster.Core;
using Npgsql;

namespace DbMaster.Adapters;

public sealed class PostgreSqlAdapter : BaseDbAdapter
{
    static PostgreSqlAdapter()
    {
        AdapterFactory.Register(cs =>
        {
            if (AdapterFactory.HasKeyword(cs, "Host") &&
                !AdapterFactory.HasKeyword(cs, "Server"))
                return new PostgreSqlAdapter(cs);
            return null;
        });
    }

    public PostgreSqlAdapter(string cs) : base(cs) { }
    public override string DbType => "postgresql";
    protected override DbConnection CreateConnection() => new NpgsqlConnection(ConnectionString);

    protected override async Task<List<TableInfo>> QueryTablesAsync(DbConnection conn, CancellationToken ct)
    {
        var tables = new List<TableInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tablename FROM pg_catalog.pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema') ORDER BY tablename";
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            using var cnt = conn.CreateCommand();
            cnt.CommandText = $"SELECT COUNT(*) FROM \"{name.Replace("\"", "\"\"")}\"";
            tables.Add(new TableInfo { Name = name, RowCount = Convert.ToInt64(await cnt.ExecuteScalarAsync(ct)) });
        }
        return tables;
    }

    protected override async Task<List<ColumnInfo>> QueryColumnsAsync(DbConnection conn, string table, CancellationToken ct)
    {
        var cols = new List<ColumnInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT column_name, data_type, is_nullable, column_default FROM information_schema.columns WHERE table_name = @table ORDER BY ordinal_position";
        cmd.Parameters.Add(new NpgsqlParameter("@table", table));
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            cols.Add(new ColumnInfo
            {
                Name = r.GetString(0), DataType = r.GetString(1),
                IsNullable = r.GetString(2) == "YES",
                DefaultValue = r.IsDBNull(3) ? null : r.GetString(3),
            });
        return cols;
    }

    protected override async Task<List<string>> QueryPrimaryKeysAsync(DbConnection conn, string table, CancellationToken ct)
    {
        var pks = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT a.attname FROM pg_index i JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey) WHERE i.indrelid = @table::regclass AND i.indisprimary";
        cmd.Parameters.Add(new NpgsqlParameter("@table", table));
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) pks.Add(r.GetString(0));
        return pks;
    }

    protected override async Task<List<ForeignKeyInfo>> QueryForeignKeysAsync(DbConnection conn, string table, CancellationToken ct)
    {
        var fks = new List<ForeignKeyInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT kcu.column_name, ccu.table_name, ccu.column_name FROM information_schema.table_constraints tc JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_name = @table";
        cmd.Parameters.Add(new NpgsqlParameter("@table", table));
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            fks.Add(new ForeignKeyInfo { ColumnName = r.GetString(0), ReferencedTable = r.GetString(1), ReferencedColumn = r.GetString(2) });
        return fks;
    }

    protected override async Task<List<IndexInfo>> QueryIndexesAsync(DbConnection conn, string table, CancellationToken ct)
    {
        var indexes = new List<IndexInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT indexname, indexdef FROM pg_indexes WHERE tablename = @table";
        cmd.Parameters.Add(new NpgsqlParameter("@table", table));
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            indexes.Add(new IndexInfo { Name = r.GetString(0), Columns = new List<string>(), IsUnique = r.GetString(1).Contains("UNIQUE") });
        return indexes;
    }
}
