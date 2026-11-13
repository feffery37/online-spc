using SqlSugar;

namespace Spc.Infrastructure;

public static class SpcDatabase
{
    public static SqlSugarScope Create(string provider, string connectionString)
    {
        var dbType = provider.Equals("Oracle", StringComparison.OrdinalIgnoreCase)
            ? DbType.Oracle
            : DbType.Sqlite;

        // Microsoft.Data.Sqlite 的连接池会共享 prepared statements 集合，多个后台 Worker
        // 并发关闭连接时可能触发 "Collection was modified" 并拖垮整个 Host。
        // 开发态 SQLite 禁用连接池（每次新连接，文件级锁由 SQLite 自身保证）；Oracle 不受影响。
        if (dbType == DbType.Sqlite &&
            !connectionString.Contains("Pooling=", StringComparison.OrdinalIgnoreCase))
        {
            connectionString = connectionString.TrimEnd(';') + ";Pooling=False";
        }

        return new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = dbType,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityNameService = (type, entity) => entity.DbTableName = DbNaming.Table(type.Name),
                EntityService = (property, column) =>
                {
                    column.DbColumnName = DbNaming.Column(property.Name);
                    if (property.Name == "Id")
                    {
                        column.IsPrimarykey = true;
                        column.IsIdentity = true;
                    }
                }
            }
        });
    }

    /// <summary>按 provider 执行对应 DDL 脚本。重复启动幂等（Oracle 忽略已存在错误）。</summary>
    public static int Initialize(SqlSugarScope db, string provider, string contentRootPath)
    {
        bool oracle = provider.Equals("Oracle", StringComparison.OrdinalIgnoreCase);
        string file = oracle ? "ddl_oracle.sql" : "ddl_sqlite.sql";
        string path = Path.Combine(AppContext.BaseDirectory, "db", file);
        if (!File.Exists(path)) path = Path.Combine(contentRootPath, "db", file);
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到 DDL 脚本：{file}", path);

        int executed = 0;
        foreach (var statement in SplitStatements(File.ReadAllText(path)))
        {
            try
            {
                db.Ado.ExecuteCommand(statement);
                executed++;
            }
            catch (Exception ex) when (
                ex.Message.Contains("ORA-00955", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("ORA-01408", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("ORA-00955", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // 已存在，忽略；便于重复启动与生产库增量执行。
            }
        }
        EnsureLimitVersionChartTypeColumn(db, oracle);
        return executed;
    }

    /// <summary>M1 已有数据库平滑升级到 M2：补 CHART_TYPE 快照列，无需删库。</summary>
    private static void EnsureLimitVersionChartTypeColumn(SqlSugarScope db, bool oracle)
    {
        try
        {
            if (oracle)
            {
                var table = db.Ado.GetDataTable(
                    "SELECT COUNT(*) FROM USER_TAB_COLUMNS WHERE TABLE_NAME = 'SPC_LIMIT_VERSION' AND COLUMN_NAME = 'CHART_TYPE'");
                if (Convert.ToInt32(table.Rows[0][0]) == 0)
                    db.Ado.ExecuteCommand("ALTER TABLE SPC_LIMIT_VERSION ADD (CHART_TYPE NUMBER(2) DEFAULT 1 NOT NULL)");
            }
            else
            {
                var table = db.Ado.GetDataTable("PRAGMA table_info(SPC_LIMIT_VERSION)");
                bool exists = table.Rows.Cast<System.Data.DataRow>()
                    .Any(r => string.Equals(r["name"]?.ToString(), "CHART_TYPE", StringComparison.OrdinalIgnoreCase));
                if (!exists)
                    db.Ado.ExecuteCommand("ALTER TABLE SPC_LIMIT_VERSION ADD COLUMN CHART_TYPE INTEGER NOT NULL DEFAULT 1");
            }
        }
        catch
        {
            // 表尚不存在或列已存在；DDL 已保证新库包含该列。
        }
    }

    private static IEnumerable<string> SplitStatements(string sql)
    {
        var current = new System.Text.StringBuilder();
        foreach (var rawLine in sql.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.StartsWith("--", StringComparison.Ordinal) || line.Length == 0) continue;
            current.AppendLine(line);
            if (line.EndsWith(';'))
            {
                var statement = current.ToString().Trim().TrimEnd(';').Trim();
                if (statement.Length > 0) yield return statement;
                current.Clear();
            }
        }
        var tail = current.ToString().Trim().TrimEnd(';').Trim();
        if (tail.Length > 0) yield return tail;
    }
}
