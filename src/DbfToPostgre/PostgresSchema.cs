using System.Text;
using NpgsqlTypes;

namespace DbfToPostgre;

internal static class PostgresSchema
{
    public static void ValidateIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0') || Encoding.UTF8.GetByteCount(name) > 63)
            throw new ArgumentException("Имя схемы, таблицы или поля должно содержать от 1 до 63 байт UTF-8 и не содержать NUL.");
    }

    public static string Quote(string name)
    {
        ValidateIdentifier(name);
        return "\"" + name.Replace("\"", "\"\"") + "\"";
    }

    public static void ValidateFields(IReadOnlyList<DbfField> fields)
    {
        if (fields.Count == 0)
            throw new InvalidDataException("В DBF нет полей.");
        var names = new HashSet<string>(StringComparer.Ordinal) { "id" };
        foreach (var field in fields)
        {
            ValidateIdentifier(field.Name);
            if (!names.Add(field.Name))
                throw new InvalidDataException($"Повторное или зарезервированное имя поля: {field.Name}");
            _ = GetType(field);
        }
    }

    public static (string Sql, NpgsqlDbType Binary) GetType(DbfField field) => field.Type switch
    {
        'C' when field.Length > 0 => ($"character varying({field.Length})", NpgsqlDbType.Varchar),
        'M' => ("text", NpgsqlDbType.Text),
        'N' when field.Length > 0 && field.DecimalCount >= 0 && field.DecimalCount <= field.Length
            => ($"numeric({field.Length},{field.DecimalCount})", NpgsqlDbType.Numeric),
        'I' => ("integer", NpgsqlDbType.Integer),
        'F' => ("real", NpgsqlDbType.Real),
        'B' => ("double precision", NpgsqlDbType.Double),
        'Y' => ("numeric(19,4)", NpgsqlDbType.Numeric),
        'D' => ("date", NpgsqlDbType.Date),
        'T' => ("timestamp without time zone", NpgsqlDbType.Timestamp),
        'L' => ("boolean", NpgsqlDbType.Boolean),
        _ => throw new NotSupportedException($"Неподдерживаемый тип DBF '{field.Type}' у поля '{field.Name}'.")
    };

    public static string CreateTableSql(string schema, string table, IReadOnlyList<DbfField> fields)
    {
        ValidateFields(fields);
        var columns = fields.Select(f => $"{Quote(f.Name)} {GetType(f).Sql}");
        return $"CREATE TABLE IF NOT EXISTS {Quote(schema)}.{Quote(table)} (\"id\" bigserial PRIMARY KEY, {string.Join(", ", columns)})";
    }
}
