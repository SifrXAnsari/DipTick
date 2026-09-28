namespace DipTick.Models;

/// <summary>
/// The databases DipTick can keep its data in: six that a person and an agent can both work in,
/// a person through a query language and a GUI, an agent through queries and vector search.
/// </summary>
/// <remarks>
/// Fact-checked against each vendor's docs on 2026-09-24. MariaDB rather than MongoDB, because
/// MongoDB's vector search on Windows runs only in a Linux container (Docker/WSL2), which means
/// changing Windows. Noted limits: SQLite's vectors come from the third-party sqlite-vec (ANN
/// still alpha); PostgreSQL needs pgvector built separately;
/// SQL Server 2025's approximate vector index is preview on-premises; DuckDB and SQLite take one
/// writer at a time and have no logins; Neo4j is a JVM server.
/// </remarks>
internal enum DatabaseKind
{
    Sqlite,
    PostgreSql,
    SqlServer,
    DuckDb,
    MariaDb,
    Neo4j,
}

internal static class DatabaseKinds
{
    /// <summary>A single file beside the config: nothing to install, nothing to run.</summary>
    public const DatabaseKind Default = DatabaseKind.Sqlite;

    public const string DefaultConnection = "diptick.db";

    private static readonly (DatabaseKind Kind, string Name)[] Table =
    [
        (DatabaseKind.Sqlite, "sqlite"),
        (DatabaseKind.PostgreSql, "postgresql"),
        (DatabaseKind.SqlServer, "sqlserver"),
        (DatabaseKind.DuckDb, "duckdb"),
        (DatabaseKind.MariaDb, "mariadb"),
        (DatabaseKind.Neo4j, "neo4j"),
    ];

    public static IEnumerable<string> Names => Table.Select(t => t.Name);

    public static string Name(DatabaseKind kind) => Array.Find(Table, t => t.Kind == kind).Name;

    public static DatabaseKind? Parse(string text)
    {
        foreach (var (kind, name) in Table)
        {
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return null;
    }
}
