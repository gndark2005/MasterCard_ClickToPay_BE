using MC_ClickToPay.Services.Checkout;
using Xunit;

namespace MC_ClickToPay.Services.Tests;

public sealed class CsvCheckoutTransactionFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"mctp-txn-{Guid.NewGuid():N}");

    private readonly UtcClock clock = new() { Now = new DateTimeOffset(2026, 10, 12, 9, 5, 7, TimeSpan.Zero) };

    [Fact]
    public async Task FirstRowOfTheDayCreatesTheFileWithTheHeader()
    {
        await File().AppendAsync(Request());

        var lines = await System.IO.File.ReadAllLinesAsync(Path.Combine(directory, "ctp_txn_20261012.csv"));
        Assert.Equal(
        [
            "date,time,spitoken,transaction_amount,transaction_currency,SrcDpaId,SrcCorrelationId,MerchantTransactionId,FlowId,XCorrelationId," +
            "checkout_event_type,checkout_event_status,confirmation_status,confirmation_reason,network_authorization_code,network_transaction_identifier",
            "20261012,090507,spi-token-1,31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-corr-1,,,,,,"
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
        Assert.Equal("20261012,090507,\"a,b\"\"c\",31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-corr-1,,,,,,", lines[1]);
    }

    [Fact]
    public async Task ConcurrentRowsAreNotInterleaved()
    {
        var file = File();
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => file.AppendAsync(Request(spiToken: $"spi-{i}"))));

        var lines = await System.IO.File.ReadAllLinesAsync(Path.Combine(directory, "ctp_txn_20261012.csv"));
        Assert.Equal(51, lines.Length);
        Assert.All(lines.Skip(1), line => Assert.Equal(16, line.Split(',').Length));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private CsvCheckoutTransactionFile File() =>
        new(new TestOptions<CheckoutTransactionFileOptions>(new() { Directory = directory }), clock);

    private static CompleteCheckoutRequest Request(string spiToken = "spi-token-1") => new()
    {
        SpiToken = spiToken,
        SrcDpaId = "dpa-1",
        SrcCorrelationId = "corr-1",
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
