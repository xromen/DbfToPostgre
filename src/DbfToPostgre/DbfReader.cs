using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text;
using DbfDataReader;
using Microsoft.Extensions.Options;

namespace DbfToPostgre;

public sealed class DbfReader(IOptions<ImportOptions> options) : IDbfReader
{
    public IDbfFile Open(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new DbfFile(path, Encoding.GetEncoding(options.Value.EncodingCodePage,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback));
    }

    private sealed class DbfFile : IDbfFile
    {
        private readonly DbfDataReader.DbfDataReader _reader;

        public DbfFile(string path, Encoding encoding)
        {
            _reader = new DbfDataReader.DbfDataReader(path, new DbfDataReaderOptions
            {
                Encoding = encoding,
                SkipDeletedRecords = true,
                StringTrimming = StringTrimmingOption.TrimEnd
            });
            try
            {
                var header = _reader.DbfTable.Header;
                // The library treats an incomplete final record as EOF; reject truncated files upfront.
                var minimumLength = header.HeaderLength + header.RecordCount * header.RecordLength;
                if (new FileInfo(path).Length < minimumLength)
                    throw new InvalidDataException($"DBF-файл обрезан: {path}");

                Fields = _reader.DbfTable.Columns.Select(c => new DbfField(
                    c.ColumnName.ToLowerInvariant(), (char)c.ColumnType, c.Length, c.DecimalCount)).ToArray();
                PostgresSchema.ValidateFields(Fields);
                for (var i = 0; i < Fields.Count; i++)
                {
                    if (Fields[i].Type == 'N')
                    {
                        var column = _reader.DbfTable.Columns[i];
                        _reader.DbfRecord.Values[i] = new NumericValue(column.Start, column.Length);
                    }
                }
            }
            catch
            {
                _reader.Dispose();
                throw;
            }
        }

        public IReadOnlyList<DbfField> Fields { get; }

        public async IAsyncEnumerable<object?[]> ReadRowsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (await _reader.ReadAsync(cancellationToken))
            {
                var values = new object?[_reader.FieldCount];
                for (var i = 0; i < values.Length; i++)
                    values[i] = _reader.IsDBNull(i) ? null : _reader.GetValue(i);
                yield return values;
            }
            // Includes deleted records: ending before the header count signals a corrupt DBF or memo.
            if (_reader.DbfTable.Header.RecordCount > 0 &&
                _reader.RecordIndex + 1 != _reader.DbfTable.Header.RecordCount)
                throw new InvalidDataException("DBF завершился раньше количества записей, указанного в заголовке.");
        }

        public void Dispose() => _reader.Dispose();
    }

    // DBF N can exceed Int64; parse as decimal and reject invalid values instead of silently returning NULL.
    private sealed class NumericValue(int start, int length) : DbfValue<decimal?>(start, length)
    {
        public override void Read(ReadOnlySpan<byte> bytes)
        {
            var text = Encoding.ASCII.GetString(bytes).Trim(' ', '\0');
            Value = text.Length == 0 ? null : decimal.Parse(text,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        }
    }
}
