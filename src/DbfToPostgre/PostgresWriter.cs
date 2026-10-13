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

        // Prevent another import or DDL from changing the schema between inspection and COPY.
        await using (var command = new NpgsqlCommand($"LOCK TABLE {table} IN SHARE ROW EXCLUSIVE MODE", connection, transaction))
            await command.ExecuteNonQueryAsync(cancellationToken);

        var targetColumns = await SynchronizeTableAsync(connection, transaction, table, fields, cancellationToken);
        var columns = string.Join(", ", targetColumns.Select(c => PostgresSchema.Quote(c.Name)));
        long count = 0;
        await using (var copy = await connection.BeginBinaryImportAsync(
            $"COPY {table} ({columns}) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            await foreach (var values in file.ReadRowsAsync(cancellationToken))
            {
                if (values.Length != fields.Count)
                    throw new InvalidDataException("Количество значений не соответствует количеству полей DBF.");
                await copy.StartRowAsync(cancellationToken);
                foreach (var column in targetColumns)
                {
                    var index = column.SourceIndex;
                    var value = index < 0 ? null : ConvertValue(values[index], types[index].Binary);
                    if (value is null)
                        await copy.WriteNullAsync(cancellationToken);
                    else
                        await copy.WriteAsync(value, types[index].Binary, cancellationToken);
                }
                count++;
            }
            await copy.CompleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    internal static object? ConvertValue(object? value, NpgsqlDbType type)
    {
        if (value is null or DBNull || value is string text && string.IsNullOrWhiteSpace(text))
            return null;
        return type switch
        {
            NpgsqlDbType.Numeric => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            NpgsqlDbType.Integer => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            NpgsqlDbType.Timestamp => DateTime.SpecifyKind((DateTime)value, DateTimeKind.Unspecified),
            _ => value
        };
    }

    private async Task<IReadOnlyList<(string Name, int SourceIndex)>> SynchronizeTableAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string table,
        IReadOnlyList<DbfField> fields, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT a.attname, pg_catalog.format_type(a.atttypid, a.atttypmod),
                   a.attnotnull,
                   pg_catalog.pg_get_expr(d.adbin, d.adrelid),
                   a.attidentity::text, a.attgenerated::text
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
        var missing = fields.Select((field, index) => (field.Name, Index: index))
            .ToDictionary(f => f.Name, f => f.Index);
        var columns = new List<(string Name, int SourceIndex)>();
        var hasId = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                var type = reader.GetString(1);
                if (name == "id")
                {
                    hasId = true;
                    if (type != "bigint" || !reader.GetBoolean(2) ||
                        (reader.IsDBNull(3) || !reader.GetString(3).StartsWith("nextval(", StringComparison.Ordinal)) &&
                        string.IsNullOrEmpty(reader.GetString(4)))
                        throw new InvalidDataException("Поле id должно быть bigint NOT NULL с автоматическим значением из последовательности.");
                    continue;
                }
                if (!string.IsNullOrEmpty(reader.GetString(5)) || !string.IsNullOrEmpty(reader.GetString(4)))
                    throw new InvalidDataException($"Нельзя записать данные или NULL в автоматически вычисляемое поле '{name}'.");

                if (missing.Remove(name, out var index))
                {
                    if (type != PostgresSchema.GetType(fields[index]).Sql)
                        throw new InvalidDataException($"Поле '{name}' существующей таблицы несовместимо с DBF (тип {type}).");
                    columns.Add((name, index));
                }
                else
                {
                    if (reader.GetBoolean(2))
                        throw new InvalidDataException($"Поле '{name}' отсутствует в DBF, но ограничение NOT NULL в таблице запрещает запись NULL.");
                    columns.Add((name, -1));
                }
            }
        }
        if (!hasId)
            throw new InvalidDataException("В таблице отсутствует поле id.");

        foreach (var (name, index) in missing)
        {
            await using var alter = new NpgsqlCommand(
                $"ALTER TABLE {table} ADD COLUMN {PostgresSchema.Quote(name)} {PostgresSchema.GetType(fields[index]).Sql}",
                connection, transaction);
            await alter.ExecuteNonQueryAsync(cancellationToken);
            columns.Add((name, index));
        }
        return columns;
    }
}
