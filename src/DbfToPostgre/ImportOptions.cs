namespace DbfToPostgre;

public sealed class ImportOptions
{
    public const string SectionName = "Import";
    public string FolderPath { get; set; } = "";
    public string ConnectionString { get; set; } = "";
    public string SchemaName { get; set; } = "public";
    public string TableName { get; set; } = "";
    public int EncodingCodePage { get; set; } = 866;
}
