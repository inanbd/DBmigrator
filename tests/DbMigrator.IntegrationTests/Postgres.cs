using Npgsql;

namespace DbMigrator.IntegrationTests;

/// <summary>Small query helpers for checking what landed in PostgreSQL.</summary>
internal static class Postgres
{
    public static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>First column → second column, for catalog lookups such as column → type.</summary>
    public static async Task<Dictionary<string, string>> PairsAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var pairs = new Dictionary<string, string>();
        while (await reader.ReadAsync())
        {
            pairs[reader.GetString(0)] = Convert.ToString(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture)!;
        }

        return pairs;
    }

    public static Task<long> CountAsync(string connectionString, string table) =>
        ScalarAsync<long>(connectionString, $"SELECT count(*) FROM {table}");
}
