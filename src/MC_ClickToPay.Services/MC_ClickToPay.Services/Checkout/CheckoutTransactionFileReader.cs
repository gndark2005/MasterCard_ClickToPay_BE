using System.Text;

namespace MC_ClickToPay.Services.Checkout;

/// <summary>One data row of the daily transaction file. <see cref="Line"/> is its line number in the file (header = 1).</summary>
public sealed class CheckoutTransactionRow
{
    /// <summary>Name of the file the row was read from (ctp_txn_YYYYMMDD.csv), when it was read from disk.</summary>
    public string? FileName { get; init; }

    public required int Line { get; init; }

    /// <summary>The PowerTranz SPI token. Never logged or returned.</summary>
    public string? SpiToken { get; init; }

    public string? TransactionAmount { get; init; }

    public string? TransactionCurrency { get; init; }

    public string? SrcCorrelationId { get; init; }

    public string? MerchantTransactionId { get; init; }

    public string? FlowId { get; init; }

    public string? XCorrelationId { get; init; }

    public string? CheckoutEventType { get; init; }

    public string? CheckoutEventStatus { get; init; }

    public string? ConfirmationStatus { get; init; }

    public string? ConfirmationReason { get; init; }

    public string? NetworkAuthorizationCode { get; init; }

    public string? NetworkTransactionIdentifier { get; init; }

    /// <summary>Result of the last confirmation to Mastercard (Confirmed or ConfirmationFailed); null when never confirmed.</summary>
    public string? ConfirmationResult { get; init; }
}

/// <summary>A transaction file cannot be read (empty, malformed or missing required columns).</summary>
public sealed class InvalidTransactionFileException(string message) : Exception(message);

/// <summary>
/// Reads the daily transaction file (see <see cref="CsvCheckoutTransactionFile"/>): RFC 4180 CSV with a header row.
/// Columns are found by name (case-insensitive), so their order does not matter and files written before the outcome
/// or confirmation result columns existed are still read (those values are null).
/// </summary>
public static class CheckoutTransactionFileReader
{
    private static readonly string[] RequiredColumns =
        ["transaction_amount", "transaction_currency", "SrcCorrelationId", "MerchantTransactionId", "FlowId", "XCorrelationId"];

    public static async Task<IReadOnlyList<CheckoutTransactionRow>> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true);
        return Read(await reader.ReadToEndAsync(cancellationToken));
    }

    public static IReadOnlyList<CheckoutTransactionRow> Read(string text, string? fileName = null)
    {
        var records = Parse(text);
        if (records.Count == 0 || records[0].Fields.All(string.IsNullOrWhiteSpace))
        {
            throw new InvalidTransactionFileException("The file is empty: a header row is required.");
        }

        var header = records[0].Fields
            .Select((name, index) => (Name: name.Trim(), Index: index))
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Index, StringComparer.OrdinalIgnoreCase);
        var missing = RequiredColumns.Where(c => !header.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidTransactionFileException($"The header is missing the column(s): {string.Join(", ", missing)}.");
        }

        var rows = new List<CheckoutTransactionRow>();
        foreach (var record in records.Skip(1))
        {
            // Blank lines (e.g. a trailing new line) are not rows.
            if (record.Fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            string? Field(string column) =>
                header.TryGetValue(column, out var index) && index < record.Fields.Count && !string.IsNullOrWhiteSpace(record.Fields[index])
                    ? record.Fields[index].Trim()
                    : null;

            rows.Add(new CheckoutTransactionRow
            {
                FileName = fileName,
                Line = record.Line,
                SpiToken = Field("spitoken"),
                TransactionAmount = Field("transaction_amount"),
                TransactionCurrency = Field("transaction_currency"),
                SrcCorrelationId = Field("SrcCorrelationId"),
                MerchantTransactionId = Field("MerchantTransactionId"),
                FlowId = Field("FlowId"),
                XCorrelationId = Field("XCorrelationId"),
                CheckoutEventType = Field("checkout_event_type"),
                CheckoutEventStatus = Field("checkout_event_status"),
                ConfirmationStatus = Field("confirmation_status"),
                ConfirmationReason = Field("confirmation_reason"),
                NetworkAuthorizationCode = Field("network_authorization_code"),
                NetworkTransactionIdentifier = Field("network_transaction_identifier"),
                ConfirmationResult = Field("confirmation_result")
            });
        }

        return rows;
    }

    internal sealed record Record(int Line, List<string> Fields);

    /// <summary>RFC 4180: quotes a value that holds a comma, quote or line break.</summary>
    internal static string Escape(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";

    // RFC 4180: quoted fields may hold commas, doubled quotes and line breaks.
    internal static List<Record> Parse(string text)
    {
        var records = new List<Record>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    records.Add(new Record(recordLine, fields));
                    fields = [];
                    line++;
                    recordLine = line;
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (quoted)
        {
            throw new InvalidTransactionFileException($"Line {recordLine} has a quoted value that is never closed.");
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(new Record(recordLine, fields));
        }

        return records;
    }
}
