using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace MC_ClickToPay.Services.Checkout;

/// <summary>
/// Daily CSV of successful /checkout decryptions. Once its outcome columns are filled in, it is uploaded to
/// POST /api/checkout/confirmations, which confirms every row to Mastercard. One file per day: ctp_txn_YYYYMMDD.csv.
/// </summary>
public interface ICheckoutTransactionFile
{
    Task AppendAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default);
}

public sealed class CheckoutTransactionFileOptions
{
    public const string SectionName = "CheckoutTransactionFile";

    /// <summary>Folder of the daily files; a relative path is resolved against the API content root.</summary>
    public string Directory { get; set; } = "transactions";
}

/// <summary>
/// Appends one row per transaction, writing the header when the day's file is created. Dates and times are the
/// server's local time. Registered as a singleton: the lock keeps concurrent requests from interleaving rows.
/// </summary>
public sealed class CsvCheckoutTransactionFile(IOptions<CheckoutTransactionFileOptions> options, TimeProvider time)
    : ICheckoutTransactionFile
{
    /// <summary>
    /// The last six columns are the payment outcome for /checkout/confirmations. They are written empty, because the
    /// outcome is not known yet when /api/checkout answers, and must be filled in after the PowerTranz payment.
    /// </summary>
    public const string Header =
        "date,time,spitoken,transaction_amount,transaction_currency,SrcDpaId,SrcCorrelationId,MerchantTransactionId,FlowId,XCorrelationId," +
        "checkout_event_type,checkout_event_status,confirmation_status,confirmation_reason,network_authorization_code,network_transaction_identifier";

    // One empty value per outcome column.
    private const string EmptyOutcome = ",,,,,,";

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
            Escape(request.XCorrelationId)) + EmptyOutcome;

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

    /// <summary>RFC 4180: quotes a value that holds a comma, quote or line break.</summary>
    private static string Escape(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";
}
