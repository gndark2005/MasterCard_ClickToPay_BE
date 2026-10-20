using System.Text;
using MC_ClickToPay.Services.Checkout;
using Xunit;

namespace MC_ClickToPay.Services.Tests;

public sealed class CheckoutTransactionFileReaderTests
{
    private const string Header =
        "date,time,spitoken,transaction_amount,transaction_currency,SrcDpaId,SrcCorrelationId,MerchantTransactionId,FlowId,XCorrelationId," +
        "checkout_event_type,checkout_event_status,confirmation_status,confirmation_reason,network_authorization_code,network_transaction_identifier";

    [Fact]
    public async Task ReadsRowsWithTheirOutcome()
    {
        var rows = await Read($"""
            {Header}
            20261015,090507,spi-1,31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-1,01,01,01,"Approved, thank you",123456,mastercard
            """);

        var row = Assert.Single(rows);
        Assert.Equal(2, row.Line);
        Assert.Equal("31.25", row.TransactionAmount);
        Assert.Equal("USD", row.TransactionCurrency);
        Assert.Equal("corr-1", row.SrcCorrelationId);
        Assert.Equal("mtid-1", row.MerchantTransactionId);
        Assert.Equal("flow-1", row.FlowId);
        Assert.Equal("x-1", row.XCorrelationId);
        Assert.Equal("01", row.ConfirmationStatus);
        Assert.Equal("Approved, thank you", row.ConfirmationReason);
        Assert.Equal("123456", row.NetworkAuthorizationCode);
        Assert.Equal("mastercard", row.NetworkTransactionIdentifier);
    }

    [Fact]
    public async Task RowsWrittenByTheApiHaveNoOutcome()
    {
        var rows = await Read($"{Header}\n20261015,090507,spi-1,31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-1,,,,,,\n");

        var row = Assert.Single(rows);
        Assert.Null(row.CheckoutEventType);
        Assert.Null(row.ConfirmationStatus);
    }

    [Fact]
    public async Task FilesFromBeforeTheOutcomeColumnsAreStillRead()
    {
        var rows = await Read("""
            date,time,spitoken,transaction_amount,transaction_currency,SrcDpaId,SrcCorrelationId,MerchantTransactionId,FlowId,XCorrelationId
            20261012,090507,spi-1,31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-1
            """);

        var row = Assert.Single(rows);
        Assert.Equal("corr-1", row.SrcCorrelationId);
        Assert.Null(row.ConfirmationStatus);
    }

    [Fact]
    public async Task ColumnsAreFoundByNameInAnyOrderAndCase()
    {
        var rows = await Read("""
            XCORRELATIONID,confirmation_status,checkout_event_status,checkout_event_type,srccorrelationid,MerchantTransactionId,FlowId,transaction_amount,transaction_currency
            x-1,02,01,01,corr-1,mtid-1,flow-1,6.00,EUR
            """);

        var row = Assert.Single(rows);
        Assert.Equal("x-1", row.XCorrelationId);
        Assert.Equal("02", row.ConfirmationStatus);
        Assert.Equal("corr-1", row.SrcCorrelationId);
        Assert.Equal("EUR", row.TransactionCurrency);
    }

    [Fact]
    public async Task QuotedValuesKeepCommasQuotesAndLineBreaksAndLineNumbersFollowTheFile()
    {
        var rows = await Read($"{Header}\r\n" +
            "20261015,090507,spi-1,31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-1,01,01,01,\"Line one\nsaid \"\"ok\"\"\",1,a\r\n" +
            "\r\n" +
            "20261015,090508,spi-2,10,USD,dpa-1,corr-2,mtid-2,flow-2,x-2,,,,,,\r\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal("Line one\nsaid \"ok\"", rows[0].ConfirmationReason);
        Assert.Equal(2, rows[0].Line);
        Assert.Equal(5, rows[1].Line);
    }

    [Fact]
    public async Task Utf8ByteOrderMarkIsIgnored()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes($"{Header}\n20261015,090507,s,1,USD,d,c,m,f,x,01,01,01,,,")).ToArray();

        var rows = await CheckoutTransactionFileReader.ReadAsync(new MemoryStream(bytes));

        Assert.Equal("c", Assert.Single(rows).SrcCorrelationId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("date,time,spitoken,transaction_amount\n20261015,090507,s,1")]
    [InlineData("date,SrcCorrelationId,MerchantTransactionId,FlowId,XCorrelationId,transaction_amount,transaction_currency\n20261015,\"unclosed,m,f,x,1,USD")]
    public async Task InvalidFilesAreRejected(string content)
    {
        await Assert.ThrowsAsync<InvalidTransactionFileException>(() => Read(content));
    }

    [Fact]
    public async Task TheMissingColumnsAreNamed()
    {
        var exception = await Assert.ThrowsAsync<InvalidTransactionFileException>(() =>
            Read("date,time,transaction_amount,transaction_currency,SrcCorrelationId\n"));

        Assert.Contains("MerchantTransactionId, FlowId, XCorrelationId", exception.Message);
    }

    private static Task<IReadOnlyList<CheckoutTransactionRow>> Read(string content) =>
        CheckoutTransactionFileReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)));
}
