using System.Globalization;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace DbfToPostgre;

public sealed class PostgresWriter(NpgsqlDataSource dataSource, IOptions<ImportOptions> options) : IPostgresWriter
{
    public async Task<long> ImportAsync(IDbfFile file, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var table = $"{PostgresSchema.Quote(settings.SchemaName)}.{PostgresSchema.Quote(settings.TableName)}";
        var fields = file.Fields;
        var types = fields.Select(PostgresSchema.GetType).ToArray();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand(
            PostgresSchema.CreateTableSql(settings.SchemaName, settings.TableName, fields), connection, transaction))
            await command.ExecuteNonQueryAsync(cancellationToken);

        await ValidateTableAsync(connection, transaction, fields, cancellationToken);
        var columns = string.Join(", ", fields.Select(f => PostgresSchema.Quote(f.Name)));
        long count = 0;
        await using (var copy = await connection.BeginBinaryImportAsync(
            $"COPY {table} ({columns}) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            await foreach (var values in file.ReadRowsAsync(cancellationToken))
            {
                if (values.Length != fields.Count)
                    throw new InvalidDataException("Количество значений не соответствует количеству полей DBF.");
                await copy.StartRowAsync(cancellationToken);
                for (var i = 0; i < values.Length; i++)
                {
                    var value = values[i];
                    if (value is null or DBNull)
                        await copy.WriteNullAsync(cancellationToken);
                    else
                        await copy.WriteAsync(ConvertValue(value, types[i].Binary), types[i].Binary, cancellationToken);
                }
                count++;
            }
            await copy.CompleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    private static object ConvertValue(object value, NpgsqlDbType type) => type switch
    {
        NpgsqlDbType.Numeric => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        NpgsqlDbType.Integer => Convert.ToInt32(value, CultureInfo.InvariantCulture),
        NpgsqlDbType.Timestamp => DateTime.SpecifyKind((DateTime)value, DateTimeKind.Unspecified),
        _ => value
    };

    private async Task ValidateTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyList<DbfField> fields, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT a.attname, pg_catalog.format_type(a.atttypid, a.atttypmod),
                   a.attnotnull,
                   pg_catalog.pg_get_expr(d.adbin, d.adrelid),
                   a.attidentity::text
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE n.nspname = @schema AND c.relname = @table
              AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("schema", options.Value.SchemaName);
        command.Parameters.AddWithValue("table", options.Value.TableName);
        var expected = fields.ToDictionary(f => f.Name, f => PostgresSchema.GetType(f).Sql);
        expected.Add("id", "bigint");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var type = reader.GetString(1);
            if (!expected.Remove(name, out var expectedType) || type != expectedType)
                throw new InvalidDataException($"Поле '{name}' существующей таблицы несовместимо с DBF (тип {type}).");
            if (name == "id" && (!reader.GetBoolean(2) ||
                (reader.IsDBNull(3) || !reader.GetString(3).StartsWith("nextval(", StringComparison.Ordinal)) &&
                string.IsNullOrEmpty(reader.GetString(4))))
                throw new InvalidDataException("Поле id должно быть bigint NOT NULL с автоматическим значением из последовательности.");
        }
        if (expected.Count != 0)
            throw new InvalidDataException($"В таблице отсутствуют поля: {string.Join(", ", expected.Keys)}.");
    }
}
