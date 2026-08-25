using System.Text;

namespace Warehouse.Wms.Application.Import;

public sealed record ImportError(
    int RowNumber,
    string Column,
    string Code,
    string Message,
    string Suggestion);

public sealed class ImportErrorReport
{
    public ImportErrorReport(IEnumerable<ImportError>? errors = null)
        => Errors = (errors ?? []).ToArray();

    public IReadOnlyList<ImportError> Errors { get; }

    public bool HasErrors => Errors.Count > 0;

    public byte[] ToCsvBytes()
    {
        var builder = new StringBuilder();
        builder.AppendLine("RowNumber,Column,Code,Message,Suggestion");
        foreach (var error in Errors)
        {
            builder.Append(error.RowNumber).Append(',')
                .Append(Escape(error.Column)).Append(',')
                .Append(Escape(error.Code)).Append(',')
                .Append(Escape(error.Message)).Append(',')
                .Append(Escape(error.Suggestion)).AppendLine();
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string Escape(string value)
        => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
