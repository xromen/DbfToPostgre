using System.Text;

namespace DbfToPostgre.Tests;

internal sealed class DbfFixture : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "DbfToPostgreTests", Guid.NewGuid().ToString("N"));

    public static readonly DbfField[] Fields =
    [
        new("NAME", 'C', 20, 0), new("AMOUNT", 'N', 20, 2),
        new("DATE", 'D', 8, 0), new("ACTIVE", 'L', 1, 0), new("COUNT", 'N', 20, 0)
    ];

    public string Write(string name = "sample.dbf", string amount = "1234.56", string count = "12345678901234567890")
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, name);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(866);
        using var writer = new BinaryWriter(File.Create(path));
        var headerLength = 32 + Fields.Length * 32 + 1;
        var recordLength = 1 + Fields.Sum(f => f.Length);
        var header = new byte[32];
        header[0] = 0x03;
        header[1] = 126; header[2] = 10; header[3] = 13;
        BitConverter.GetBytes(3).CopyTo(header, 4);
        BitConverter.GetBytes((ushort)headerLength).CopyTo(header, 8);
        BitConverter.GetBytes((ushort)recordLength).CopyTo(header, 10);
        header[29] = 0x65;
        writer.Write(header);
        foreach (var field in Fields)
        {
            var descriptor = new byte[32];
            Encoding.ASCII.GetBytes(field.Name).CopyTo(descriptor, 0);
            descriptor[11] = (byte)field.Type;
            descriptor[16] = (byte)field.Length;
            descriptor[17] = (byte)field.DecimalCount;
            writer.Write(descriptor);
        }
        writer.Write((byte)0x0D);
        WriteRow(false, ["Привет, мир", amount, "20261013", "T", count]);
        WriteRow(false, ["  Ёжик", "", "", "?", ""]);
        WriteRow(true, ["Удалён", "99.50", "20260101", "F", "10"]);
        writer.Write((byte)0x1A);
        return path;

        void WriteRow(bool deleted, string[] values)
        {
            writer.Write(deleted ? (byte)'*' : (byte)' ');
            for (var i = 0; i < Fields.Length; i++)
            {
                var value = Fields[i].Type == 'N' ? values[i].PadLeft(Fields[i].Length) : values[i].PadRight(Fields[i].Length);
                writer.Write(encoding.GetBytes(value));
            }
        }
    }

    public void Dispose() => Directory.Delete(Folder, recursive: true);
}
