using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Shabana.Data.Maintenance;

/// <summary>
/// SHA-256 fingerprints of a database, used to PROVE that a migration or a restore did what it should and nothing else.
/// All are computed with one deterministic row order and a type-tagged, length-prefixed encoding (no ambiguity between
/// <c>("ab","c")</c> and <c>("a","bc")</c>, between 1 and '1', or between NULL and '').
/// </summary>
public static class DatabaseFingerprint
{
    /// <summary>
    /// The EXACT schema: type, name, owning table and original SQL text of every table, index, trigger and view (SQLite's own
    /// bookkeeping tables excluded). Two databases with the same schema fingerprint were built by the same statements.
    /// </summary>
    public static string Schema(SqliteConnection connection)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendRows(hash, connection,
            "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY type, name");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Every row of every user table (ordered by primary key / rowid). Equal data fingerprints mean equal logical content.</summary>
    public static string Data(SqliteConnection connection)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var table in UserTables(connection))
        {
            Append(hash, "table");
            Append(hash, table.Name);
            AppendRows(hash, connection, $"SELECT * FROM {Quote(table.Name)} ORDER BY {table.OrderBy}");
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// The numbers the owner cares about, read from the views (04 section 8.2 step 3): stock per branch and item, cash per drawer,
    /// contact balances, wallets, order totals and paid amounts, PO conversion progress. A migration may not change any of them
    /// unless it declares <c>-- shabana:balances-may-change</c>.
    /// </summary>
    public static string Balances(SqliteConnection connection)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var sql in BalanceQueries)
        {
            Append(hash, "query");
            AppendRows(hash, connection, sql);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static readonly string[] BalanceQueries =
    [
        "SELECT site_id, item_id, qty_milli FROM v_stock_balance ORDER BY site_id, item_id",
        "SELECT site_id, balance_piasters FROM v_cash_drawer_balance ORDER BY site_id",
        "SELECT contact_id, receivable_piasters, payable_piasters, client_credit_piasters, vendor_credit_piasters, opening_net_piasters, balance_piasters FROM v_contact_balance ORDER BY contact_id",
        "SELECT contact_id, balance_piasters FROM v_client_credit_balance ORDER BY contact_id",
        "SELECT contact_id, balance_piasters FROM v_vendor_credit_balance ORDER BY contact_id",
        "SELECT so_id, total_piasters, paid_piasters FROM v_so_balance ORDER BY so_id",
        "SELECT po_id, total_piasters, paid_piasters FROM v_po_balance ORDER BY po_id",
        "SELECT po_id, total_kg_milli, converted_kg_milli FROM v_po_kg_progress ORDER BY po_id",
    ];

    /// <summary>User tables with the ORDER BY that makes their row order deterministic.</summary>
    internal static List<(string Name, string OrderBy)> UserTables(SqliteConnection connection)
    {
        var tables = new List<(string Name, string Sql)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tables.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var result = new List<(string, string)>();
        foreach (var (name, sql) in tables)
        {
            if (sql.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase))
            {
                var keys = new List<string>();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT name FROM pragma_table_info({QuoteLiteral(name)}) WHERE pk > 0 ORDER BY pk";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    keys.Add(Quote(reader.GetString(0)));
                }

                result.Add((name, string.Join(", ", keys)));
            }
            else
            {
                result.Add((name, "rowid"));
            }
        }

        return result;
    }

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string QuoteLiteral(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static void AppendRows(IncrementalHash hash, SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var count = reader.FieldCount;
        while (reader.Read())
        {
            Append(hash, "row");
            for (var i = 0; i < count; i++)
            {
                if (reader.IsDBNull(i))
                {
                    Append(hash, "N");
                    continue;
                }

                switch (reader.GetFieldType(i).Name)
                {
                    case nameof(Int64):
                        Append(hash, "I" + reader.GetInt64(i).ToString(CultureInfo.InvariantCulture));
                        break;
                    case nameof(Double):
                        Append(hash, "R" + BitConverter.DoubleToInt64Bits(reader.GetDouble(i)).ToString(CultureInfo.InvariantCulture));
                        break;
                    case nameof(Byte) + "[]":
                        hash.AppendData(Encoding.ASCII.GetBytes("B"));
                        var bytes = (byte[])reader.GetValue(i);
                        hash.AppendData(BitConverter.GetBytes(bytes.Length));
                        hash.AppendData(bytes);
                        break;
                    default:
                        Append(hash, "T" + reader.GetString(i));
                        break;
                }
            }
        }
    }

    private static void Append(IncrementalHash hash, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}
