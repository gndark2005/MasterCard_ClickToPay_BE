using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MC_ClickToPay.Services.Checkout;

/// <summary>
/// Daily CSV of successful /checkout decryptions, one file per day: ctp_txn_YYYYMMDD.csv. After the PowerTranz payment,
/// POST /api/confirmations/{spiToken} confirms the row to Mastercard and writes the outcome and result back to it.
/// </summary>
public interface ICheckoutTransactionFile
{
    Task AppendAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default);

    /// <summary>The latest row with this SPI token, searching the newest file first; null when there is none.</summary>
    Task<CheckoutTransactionRow?> FindAsync(string spiToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a confirmation into its row: the payment outcome columns (from <paramref name="row"/>) and the
    /// confirmation result columns.
    /// </summary>
    Task RecordConfirmationAsync(CheckoutTransactionRow row, TransactionFileConfirmation confirmation,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of a confirmation as written to the file. <see cref="Result"/> is a <see cref="CheckoutTransactionStatus"/>.</summary>
public sealed record TransactionFileConfirmation(string Result, string? Detail);

public sealed class CheckoutTransactionFileOptions
{
    public const string SectionName = "CheckoutTransactionFile";

    /// <summary>Folder of the daily files; a relative path is resolved against the API content root.</summary>
    public string Directory { get; set; } = "transactions";
}

/// <summary>
/// Appends one row per transaction, writing the header when the day's file is created, and rewrites a row when it is
/// confirmed. Dates and times are the server's local time. Registered as a singleton: the lock keeps concurrent
/// requests from interleaving rows or losing an update.
/// </summary>
public sealed class CsvCheckoutTransactionFile(
    IOptions<CheckoutTransactionFileOptions> options, TimeProvider time, ILogger<CsvCheckoutTransactionFile> logger)
    : ICheckoutTransactionFile
{
    /// <summary>
    /// Columns checkout_event_type to network_transaction_identifier are the payment outcome for
    /// /checkout/confirmations: written empty, because the outcome is not known yet when /api/checkout answers. The
    /// confirmation fills them in with the PowerTranz result it receives, and writes the last four.
    /// </summary>
    public const string Header =
        "date,time,spitoken,transaction_amount,transaction_currency,SrcDpaId,SrcCorrelationId,MerchantTransactionId,FlowId,XCorrelationId," +
        "checkout_event_type,checkout_event_status,confirmation_status,confirmation_reason,network_authorization_code,network_transaction_identifier," +
        "confirmation_result,confirmation_date,confirmation_time,confirmation_detail";

    private const string FilePattern = "ctp_txn_*.csv";

    // One empty value per outcome and confirmation result column.
    private const string EmptyColumns = ",,,,,,,,,,";

    private readonly SemaphoreSlim gate = new(1, 1);

    public static string FileName(DateTimeOffset date) =>
        $"ctp_txn_{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.csv";

    public async Task AppendAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        var now = time.GetLocalNow();
        var row = string.Join(',',
            now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            now.ToString("HHmmss", CultureInfo.InvariantCulture),
            Escape(request.SpiToken),
            request.TransactionAmount.ToString(CultureInfo.InvariantCulture),
            Escape(request.TransactionCurrencyCode),
            Escape(request.SrcDpaId),
            Escape(request.SrcCorrelationId),
            Escape(request.MerchantTransactionId),
            Escape(request.FlowId),
            Escape(request.XCorrelationId)) + EmptyColumns;

        var directory = options.Value.Directory;
        var path = Path.Combine(directory, FileName(now));

        await gate.WaitAsync(cancellationToken);
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (stream.Length == 0)
            {
                await writer.WriteLineAsync(Header);
            }

            await writer.WriteLineAsync(row);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CheckoutTransactionRow?> FindAsync(string spiToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(spiToken);
        var directory = options.Value.Directory;
        if (!System.IO.Directory.Exists(directory))
        {
            return null;
        }

        // ctp_txn_YYYYMMDD.csv: the name orders the files by day, so the newest is searched first.
        var paths = System.IO.Directory.EnumerateFiles(directory, FilePattern)
            .Where(p => p.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var fileName = Path.GetFileName(path);
            IReadOnlyList<CheckoutTransactionRow> rows;
            try
            {
                rows = CheckoutTransactionFileReader.Read(await ReadSharedAsync(path, cancellationToken), fileName);
            }
            catch (InvalidTransactionFileException ex)
            {
                logger.LogWarning("Transaction file {FileName} skipped: {Message}", fileName, ex.Message);
                continue;
            }

            var row = rows.LastOrDefault(r => string.Equals(r.SpiToken, spiToken, StringComparison.Ordinal));
            if (row is not null)
            {
                return row;
            }
        }

        return null;
    }

    public async Task RecordConfirmationAsync(CheckoutTransactionRow row, TransactionFileConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(confirmation);
        if (row.FileName is null || row.SpiToken is null)
        {
            throw new ArgumentException("The row was not read from a transaction file.", nameof(row));
        }

        var now = time.GetLocalNow();
        var path = Path.Combine(options.Value.Directory, row.FileName);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var records = CheckoutTransactionFileReader.Parse(await ReadSharedAsync(path, cancellationToken));
            var header = records[0].Fields;

            // Files written before the confirmation result columns existed get them added to the header.
            int Column(string name)
            {
                var index = header.FindIndex(h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    header.Add(name);
                    index = header.Count - 1;
                }

                return index;
            }

            var spiToken = Column("spitoken");
            var record = records.Skip(1).FirstOrDefault(r => r.Line == row.Line)
                ?? throw new InvalidOperationException($"Line {row.Line} of {row.FileName} no longer exists.");
            if (spiToken >= record.Fields.Count || record.Fields[spiToken].Trim() != row.SpiToken)
            {
                throw new InvalidOperationException($"Line {row.Line} of {row.FileName} no longer holds the transaction.");
            }

            void Set(string column, string? value)
            {
                var index = Column(column);
                while (record.Fields.Count <= index)
                {
                    record.Fields.Add(string.Empty);
                }

                record.Fields[index] = value ?? string.Empty;
            }

            // The payment outcome the confirmation sent, then its result.
            Set("checkout_event_type", row.CheckoutEventType);
            Set("checkout_event_status", row.CheckoutEventStatus);
            Set("confirmation_status", row.ConfirmationStatus);
            Set("confirmation_reason", row.ConfirmationReason);
            Set("network_authorization_code", row.NetworkAuthorizationCode);
            Set("network_transaction_identifier", row.NetworkTransactionIdentifier);
            Set("confirmation_result", confirmation.Result);
            Set("confirmation_date", now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Set("confirmation_time", now.ToString("HHmmss", CultureInfo.InvariantCulture));
            Set("confirmation_detail", confirmation.Detail);

            // Written next to the file and then swapped in, so a failure never leaves it half written.
            var text = new StringBuilder();
            foreach (var r in records)
            {
                text.Append(string.Join(',', r.Fields.Select(Escape))).Append(Environment.NewLine);
            }

            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            gate.Release();
        }
    }

    // Shared read/write: other processes (or the batch) may have the file open.
    private static async Task<string> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string Escape(string value) => CheckoutTransactionFileReader.Escape(value);
}
