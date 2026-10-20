using MC_ClickToPay.Services.Checkout;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MC_ClickToPay.Services.Tests;

public sealed class CsvCheckoutTransactionFileTests : IDisposable
{
    private const string OutcomeHeader =
        "date,time,spitoken,transaction_amount,transaction_currency,SrcDpaId,SrcCorrelationId,MerchantTransactionId,FlowId,XCorrelationId," +
        "checkout_event_type,checkout_event_status,confirmation_status,confirmation_reason,network_authorization_code,network_transaction_identifier";

    private readonly string directory = Path.Combine(Path.GetTempPath(), $"mctp-txn-{Guid.NewGuid():N}");

    private readonly UtcClock clock = new() { Now = new DateTimeOffset(2026, 10, 12, 9, 5, 7, TimeSpan.Zero) };

    [Fact]
    public async Task FirstRowOfTheDayCreatesTheFileWithTheHeader()
    {
        await File().AppendAsync(Request());

        var lines = await System.IO.File.ReadAllLinesAsync(Path.Combine(directory, "ctp_txn_20261012.csv"));
        Assert.Equal(
        [
            OutcomeHeader + ",confirmation_result,confirmation_date,confirmation_time,confirmation_detail",
            "20261012,090507,spi-token-1,31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-corr-1,,,,,,,,,,"
        ], lines);
    }

    [Fact]
    public async Task RowsOfTheSameDayAreAppendedUnderOneHeader()
    {
        var file = File();
        await file.AppendAsync(Request());
        clock.Now = clock.Now.AddHours(14);
        await file.AppendAsync(Request(spiToken: "spi-token-2"));

        var lines = await System.IO.File.ReadAllLinesAsync(Path.Combine(directory, "ctp_txn_20261012.csv"));
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("20261012,230507,spi-token-2,", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachDayHasItsOwnFile()
    {
        var file = File();
        await file.AppendAsync(Request());
        clock.Now = clock.Now.AddDays(1);
        await file.AppendAsync(Request());

        Assert.Equal(2, (await System.IO.File.ReadAllLinesAsync(Path.Combine(directory, "ctp_txn_20261012.csv"))).Length);
        Assert.Equal(2, (await System.IO.File.ReadAllLinesAsync(Path.Combine(directory, "ctp_txn_20261013.csv"))).Length);
    }

    [Fact]
    public async Task ValuesWithCommasOrQuotesAreQuoted()
    {
        await File().AppendAsync(Request(spiToken: "a,b\"c"));

        var lines = await System.IO.File.ReadAllLinesAsync(Path.Combine(directory, "ctp_txn_20261012.csv"));
        Assert.Equal("20261012,090507,\"a,b\"\"c\",31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-corr-1,,,,,,,,,,", lines[1]);
    }

    [Fact]
    public async Task ConcurrentRowsAreNotInterleaved()
    {
        var file = File();
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => file.AppendAsync(Request(spiToken: $"spi-{i}"))));

        var lines = await System.IO.File.ReadAllLinesAsync(Path.Combine(directory, "ctp_txn_20261012.csv"));
        Assert.Equal(51, lines.Length);
        Assert.All(lines.Skip(1), line => Assert.Equal(20, line.Split(',').Length));
    }

    [Fact]
    public async Task FindReturnsTheRowOfTheSpiTokenFromTheNewestFile()
    {
        var file = File();
        await file.AppendAsync(Request(spiToken: "spi-1", correlationId: "corr-old"));
        await file.AppendAsync(Request(spiToken: "spi-2"));
        clock.Now = clock.Now.AddDays(1);
        await file.AppendAsync(Request(spiToken: "spi-1", correlationId: "corr-new"));

        var row = await file.FindAsync("spi-1");

        Assert.NotNull(row);
        Assert.Equal("ctp_txn_20261013.csv", row.FileName);
        Assert.Equal(2, row.Line);
        Assert.Equal("corr-new", row.SrcCorrelationId);
        Assert.Null(await file.FindAsync("spi-unknown"));
    }

    [Fact]
    public async Task FindWithoutTheFolderReturnsNull()
    {
        Assert.Null(await File().FindAsync("spi-1"));
    }

    [Fact]
    public async Task FindSkipsUnreadableFiles()
    {
        Directory.CreateDirectory(directory);
        await System.IO.File.WriteAllTextAsync(Path.Combine(directory, "ctp_txn_20261013.csv"), "not,a,transaction,file\n");
        var file = File();
        await file.AppendAsync(Request(spiToken: "spi-1"));

        Assert.Equal("ctp_txn_20261012.csv", (await file.FindAsync("spi-1"))?.FileName);
    }

    [Fact]
    public async Task RecordConfirmationUpdatesOnlyThatRow()
    {
        var file = File();
        await file.AppendAsync(Request(spiToken: "spi-1"));
        await file.AppendAsync(Request(spiToken: "spi-2", correlationId: "corr-2"));
        var row = (await file.FindAsync("spi-2"))!;
        clock.Now = clock.Now.AddHours(1);

        await file.RecordConfirmationAsync(row, new TransactionFileConfirmation("ConfirmationFailed", "[NOT_FOUND] Transaction data, not found"));

        var lines = await System.IO.File.ReadAllLinesAsync(Path.Combine(directory, "ctp_txn_20261012.csv"));
        Assert.Equal(3, lines.Length);
        Assert.Equal(CsvCheckoutTransactionFile.Header, lines[0]);
        Assert.Equal("20261012,090507,spi-1,31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-corr-1,,,,,,,,,,", lines[1]);
        Assert.Equal("20261012,090507,spi-2,31.25,USD,dpa-1,corr-2,mtid-1,flow-1,x-corr-1,,,,,,,ConfirmationFailed,20261012,100507," +
            "\"[NOT_FOUND] Transaction data, not found\"", lines[2]);
        Assert.Equal("ConfirmationFailed", (await file.FindAsync("spi-2"))!.ConfirmationResult);
    }

    [Fact]
    public async Task RecordConfirmationAddsTheResultColumnsToOlderFiles()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ctp_txn_20261010.csv");
        await System.IO.File.WriteAllTextAsync(path,
            $"{OutcomeHeader}\n20261010,080000,spi-1,10.00,USD,dpa-1,corr-1,mtid-1,flow-1,x-1,01,01,01,\"Approved, thanks\",123456,mastercard\n");
        var file = File();

        await file.RecordConfirmationAsync((await file.FindAsync("spi-1"))!, new TransactionFileConfirmation("Confirmed", null));

        var lines = await System.IO.File.ReadAllLinesAsync(path);
        Assert.Equal(CsvCheckoutTransactionFile.Header, lines[0]);
        Assert.Equal("20261010,080000,spi-1,10.00,USD,dpa-1,corr-1,mtid-1,flow-1,x-1,01,01,01,\"Approved, thanks\",123456,mastercard," +
            "Confirmed,20261012,090507,", lines[1]);
    }

    [Fact]
    public async Task RowsAppendedAfterAnUpdateStayInTheFile()
    {
        var file = File();
        await file.AppendAsync(Request(spiToken: "spi-1"));
        await file.RecordConfirmationAsync((await file.FindAsync("spi-1"))!, new TransactionFileConfirmation("Confirmed", null));
        await file.AppendAsync(Request(spiToken: "spi-2"));

        Assert.Equal(3, (await System.IO.File.ReadAllLinesAsync(Path.Combine(directory, "ctp_txn_20261012.csv"))).Length);
        Assert.Equal(3, (await file.FindAsync("spi-2"))!.Line);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private CsvCheckoutTransactionFile File() =>
        new(new TestOptions<CheckoutTransactionFileOptions>(new() { Directory = directory }), clock,
            NullLogger<CsvCheckoutTransactionFile>.Instance);

    private static CompleteCheckoutRequest Request(string spiToken = "spi-token-1", string correlationId = "corr-1") => new()
    {
        SpiToken = spiToken,
        SrcDpaId = "dpa-1",
        SrcCorrelationId = correlationId,
        MerchantTransactionId = "mtid-1",
        FlowId = "flow-1",
        TransactionAmount = 31.25m,
        TransactionCurrencyCode = "USD",
        XCorrelationId = "x-corr-1"
    };

    /// <summary>Local time is UTC so file names and times do not depend on the test machine's time zone.</summary>
    private sealed class UtcClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
