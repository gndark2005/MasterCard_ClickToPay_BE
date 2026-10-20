using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Confirmations;
using MC_ClickToPay.Services.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MC_ClickToPay.Services.Tests;

public sealed class TransactionConfirmationServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"mctp-txn-{Guid.NewGuid():N}");

    private static readonly PaymentOutcome Approved = new()
    {
        Approved = true,
        AuthorizationCode = "123456",
        ResponseMessage = "Approved, thank you",
        NetworkTransactionIdentifier = "mastercard"
    };

    private readonly RecordingStore store = new();

    [Fact]
    public async Task TheRowOfTheSpiTokenIsSentWithTheFileData()
    {
        await WriteFileAsync("ctp_txn_20261015.csv",
            Row("spi-other", "corr-other"),
            Row("spi-1", "corr-1"));
        var checkout = new FakeCheckout(_ => new CheckoutConfirmationResult { FlowId = "flow-back" });

        var result = await Service(checkout).ConfirmAsync("spi-1", Approved, CancellationToken.None);

        Assert.Equal(TransactionConfirmationStatus.Confirmed, result.Status);
        Assert.Equal("ctp_txn_20261015.csv", result.FileName);
        Assert.Equal(3, result.Line);
        Assert.Equal("corr-1", result.CorrelationId);
        Assert.Equal("x-1", result.XCorrelationId);
        Assert.Equal("flow-back", result.FlowId);

        var sent = Assert.Single(checkout.Requests);
        Assert.Equal("corr-1", sent.CorrelationId);
        Assert.Equal("mtid-1", sent.MerchantTransactionId);
        Assert.Equal("flow-1", sent.FlowId);
        Assert.Equal("x-1", sent.XCorrelationId);
        Assert.Equal("01", sent.ConfirmationData!.CheckoutEventType);
        Assert.Equal("01", sent.ConfirmationData.CheckoutEventStatus);
        Assert.Equal("01", sent.ConfirmationData.ConfirmationStatus);
        Assert.Equal("Approved, thank you", sent.ConfirmationData.ConfirmationReason);
        Assert.Equal("123456", sent.ConfirmationData.NetworkAuthorizationCode);
        Assert.Equal("mastercard", sent.ConfirmationData.NetworkTransactionIdentifier);
        Assert.Equal(31.25m, sent.ConfirmationData.TransactionAmount!.Amount);
        Assert.Equal("USD", sent.ConfirmationData.TransactionAmount.CurrencyCode);
        Assert.Null(sent.ConfirmationData.ConfirmationTimestamp);
    }

    [Fact]
    public async Task AConfirmationIsWrittenToTheRowAndTheDatabase()
    {
        // Rows as /api/checkout writes them: no outcome yet.
        await WriteFileAsync("ctp_txn_20261015.csv",
            "20261015,090507,spi-1,31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-1,,,,,,,,,,",
            "20261015,090508,spi-2,31.25,USD,dpa-1,corr-2,mtid-1,flow-1,x-1,,,,,,,,,,");
        var checkout = new FakeCheckout(_ => new CheckoutConfirmationResult { FlowId = "flow-back" });

        await Service(checkout).ConfirmAsync("spi-1", Approved, CancellationToken.None);

        var rows = await ReadFileAsync("ctp_txn_20261015.csv");
        Assert.Equal("Confirmed", rows[0].ConfirmationResult);
        Assert.Equal(("01", "01", "01"), (rows[0].CheckoutEventType, rows[0].CheckoutEventStatus, rows[0].ConfirmationStatus));
        Assert.Equal("Approved, thank you", rows[0].ConfirmationReason);
        Assert.Equal("123456", rows[0].NetworkAuthorizationCode);
        Assert.Equal("mastercard", rows[0].NetworkTransactionIdentifier);
        Assert.Null(rows[1].ConfirmationResult);
        Assert.Null(rows[1].NetworkAuthorizationCode);
        var (request, outcome) = Assert.Single(store.Confirmations);
        Assert.Equal("corr-1", request.CorrelationId);
        Assert.True(outcome.Confirmed);
        Assert.Equal("flow-back", outcome.FlowId);
    }

    [Theory]
    [InlineData(400, 400)]
    [InlineData(404, 404)]
    [InlineData(401, 502)]
    [InlineData(null, 502)]
    public async Task AMastercardFailureIsRecordedAndCanBeRetried(int? mastercardStatus, int expected)
    {
        await WriteFileAsync("ctp_txn_20261015.csv", Row("spi-1", "corr-1"));
        var fails = true;
        var checkout = new FakeCheckout(_ => fails
            ? throw new MastercardCheckoutException("[REASON] Description", mastercardStatus)
            : new CheckoutConfirmationResult());

        var result = await Service(checkout).ConfirmAsync("spi-1", Approved, CancellationToken.None);

        Assert.Equal(TransactionConfirmationStatus.Failed, result.Status);
        Assert.Equal(expected, result.HttpStatus);
        Assert.Contains("[REASON] Description", result.Detail);
        Assert.Equal("ConfirmationFailed", (await ReadFileAsync("ctp_txn_20261015.csv"))[0].ConfirmationResult);
        Assert.False(Assert.Single(store.Confirmations).Outcome.Confirmed);

        fails = false;
        Assert.Equal(TransactionConfirmationStatus.Confirmed, (await Service(checkout).ConfirmAsync("spi-1", Approved, CancellationToken.None)).Status);
        Assert.Equal("Confirmed", (await ReadFileAsync("ctp_txn_20261015.csv"))[0].ConfirmationResult);
    }

    [Fact]
    public async Task AConfirmedRowIsNotSentAgain()
    {
        await WriteFileAsync("ctp_txn_20261015.csv", Row("spi-1", "corr-1"));
        var checkout = new FakeCheckout(_ => new CheckoutConfirmationResult());
        await Service(checkout).ConfirmAsync("spi-1", Approved, CancellationToken.None);

        var result = await Service(checkout).ConfirmAsync("spi-1", Approved, CancellationToken.None);

        Assert.Equal(TransactionConfirmationStatus.AlreadyConfirmed, result.Status);
        Assert.Single(checkout.Requests);
    }

    [Fact]
    public async Task AnUnknownSpiTokenIsNotFound()
    {
        await WriteFileAsync("ctp_txn_20261015.csv", Row("spi-1", "corr-1"));
        var checkout = new FakeCheckout(_ => new CheckoutConfirmationResult());

        var result = await Service(checkout).ConfirmAsync("spi-unknown", Approved, CancellationToken.None);

        Assert.Equal(TransactionConfirmationStatus.NotFound, result.Status);
        Assert.Empty(checkout.Requests);
    }

    [Fact]
    public async Task TheOutcomeIsTheOneReceivedAndIsWrittenToTheRow()
    {
        // A row as /api/checkout writes it: no outcome yet.
        await WriteFileAsync("ctp_txn_20261015.csv",
            "20261015,090507,spi-1,31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-1,,,,,,,,,,");
        var checkout = new FakeCheckout(_ => new CheckoutConfirmationResult());
        var declined = new PaymentOutcome
        {
            Approved = false,
            ResponseMessage = "Declined, do not honour",
            NetworkTransactionIdentifier = "mastercard"
        };

        var result = await Service(checkout).ConfirmAsync("spi-1", declined, CancellationToken.None);

        Assert.Equal(TransactionConfirmationStatus.Confirmed, result.Status);
        var data = Assert.Single(checkout.Requests).ConfirmationData!;
        Assert.Equal(("01", "02", "02"), (data.CheckoutEventType, data.CheckoutEventStatus, data.ConfirmationStatus));
        Assert.Equal("Declined, do not honour", data.ConfirmationReason);
        Assert.Null(data.NetworkAuthorizationCode);

        var row = (await ReadFileAsync("ctp_txn_20261015.csv"))[0];
        Assert.Equal(("01", "02", "02"), (row.CheckoutEventType, row.CheckoutEventStatus, row.ConfirmationStatus));
        Assert.Equal("Declined, do not honour", row.ConfirmationReason);
        Assert.Null(row.NetworkAuthorizationCode);
        Assert.Equal("mastercard", row.NetworkTransactionIdentifier);
        Assert.Equal("Confirmed", row.ConfirmationResult);
    }

    [Fact]
    public async Task TheResponseMessageIsCleanedToWhatMastercardAccepts()
    {
        await WriteFileAsync("ctp_txn_20261015.csv",
            "20261015,090507,spi-1,31.25,USD,dpa-1,corr-1,mtid-1,flow-1,x-1,,,,,,,,,,");
        var checkout = new FakeCheckout(_ => new CheckoutConfirmationResult());
        var payment = new PaymentOutcome
        {
            Approved = false,
            // Characters outside ^[0-9a-zA-Z ,;!?.-_]*$ are dropped (#, (, ), ' and -, since ".-_" is a range), double
            // spaces are collapsed, then it is cut to 64.
            ResponseMessage = "Declined (do not honour) #05 - card holder's issuer refused the transaction, please retry later"
        };

        await Service(checkout).ConfirmAsync("spi-1", payment, CancellationToken.None);

        var reason = Assert.Single(checkout.Requests).ConfirmationData!.ConfirmationReason!;
        Assert.Equal("Declined do not honour 05 card holders issuer refused the transa", reason);
        Assert.True(reason.Length <= 64);
        Assert.Equal("UNAVLB", checkout.Requests[0].ConfirmationData!.NetworkTransactionIdentifier);
        Assert.Equal(reason, (await ReadFileAsync("ctp_txn_20261015.csv"))[0].ConfirmationReason);
    }

    [Theory]
    [InlineData("0", "USD")]
    [InlineData("12,50", "USD")]
    [InlineData("1.234", "USD")]
    [InlineData("10", "840")]
    [InlineData("10", "")]
    public async Task AnInvalidRowIsNotSent(string amount, string currency)
    {
        await WriteFileAsync("ctp_txn_20261015.csv",
            $"20261015,090507,spi-1,\"{amount}\",{currency},dpa-1,corr-1,mtid-1,flow-1,x-1,,,,,,,,,,");
        var checkout = new FakeCheckout(_ => new CheckoutConfirmationResult());

        var result = await Service(checkout).ConfirmAsync("spi-1", Approved, CancellationToken.None);

        Assert.Equal(TransactionConfirmationStatus.Invalid, result.Status);
        Assert.Empty(checkout.Requests);
    }

    [Fact]
    public async Task NonMastercardErrorsPropagate()
    {
        await WriteFileAsync("ctp_txn_20261015.csv", Row("spi-1", "corr-1"));
        var checkout = new FakeCheckout(_ => throw new InvalidOperationException("signing key unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(checkout).ConfirmAsync("spi-1", Approved, CancellationToken.None));
        Assert.Null((await ReadFileAsync("ctp_txn_20261015.csv"))[0].ConfirmationResult);
    }

    [Fact]
    public async Task ADatabaseErrorDoesNotChangeTheResult()
    {
        await WriteFileAsync("ctp_txn_20261015.csv", Row("spi-1", "corr-1"));
        store.Failure = new InvalidOperationException("database down");

        var result = await Service(new FakeCheckout(_ => new CheckoutConfirmationResult())).ConfirmAsync("spi-1", Approved, CancellationToken.None);

        Assert.Equal(TransactionConfirmationStatus.Confirmed, result.Status);
        Assert.Equal("Confirmed", (await ReadFileAsync("ctp_txn_20261015.csv"))[0].ConfirmationResult);
    }

    [Fact]
    public async Task ASecondRequestForTheSameSpiTokenIsRefusedWhileTheFirstRuns()
    {
        await WriteFileAsync("ctp_txn_20261015.csv", Row("spi-concurrent", "corr-1"));
        var release = new TaskCompletionSource();
        var checkout = new FakeCheckout(_ => new CheckoutConfirmationResult(), release.Task);

        var first = Service(checkout).ConfirmAsync("spi-concurrent", Approved, CancellationToken.None);
        await checkout.Called.Task;
        var second = await Service(checkout).ConfirmAsync("spi-concurrent", Approved, CancellationToken.None);
        release.SetResult();

        Assert.Equal(TransactionConfirmationStatus.InProgress, second.Status);
        Assert.Equal(TransactionConfirmationStatus.Confirmed, (await first).Status);
        Assert.Single(checkout.Requests);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Row(string spiToken, string correlationId) =>
        $"20261015,090507,{spiToken},31.25,USD,dpa-1,{correlationId},mtid-1,flow-1,x-1,01,01,01,\"Approved, thank you\",123456,mastercard,,,,";

    private async Task WriteFileAsync(string fileName, params string[] rows)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, fileName),
            string.Join('\n', [CsvCheckoutTransactionFile.Header, .. rows]) + "\n");
    }

    private async Task<IReadOnlyList<CheckoutTransactionRow>> ReadFileAsync(string fileName) =>
        CheckoutTransactionFileReader.Read(await File.ReadAllTextAsync(Path.Combine(directory, fileName)));

    private TransactionConfirmationService Service(FakeCheckout checkout) =>
        new(checkout,
            new CsvCheckoutTransactionFile(new TestOptions<CheckoutTransactionFileOptions>(new() { Directory = directory }),
                TimeProvider.System, NullLogger<CsvCheckoutTransactionFile>.Instance),
            new CheckoutStateRecorder(store, NullLogger<CheckoutStateRecorder>.Instance),
            NullLogger<TransactionConfirmationService>.Instance);

    private sealed class RecordingStore : ICheckoutTransactionStore
    {
        public List<(CheckoutConfirmationRequest Request, ConfirmationOutcome Outcome)> Confirmations { get; } = [];

        public Exception? Failure { get; set; }

        public Task RecordCheckoutAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordConfirmationAsync(CheckoutConfirmationRequest request, ConfirmationOutcome outcome,
            CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Confirmations.Add((request, outcome));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCheckout(Func<CheckoutConfirmationRequest, CheckoutConfirmationResult> confirm, Task? gate = null)
        : IMastercardCheckoutService
    {
        private readonly List<CheckoutConfirmationRequest> requests = [];

        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<CheckoutConfirmationRequest> Requests
        {
            get
            {
                lock (requests)
                {
                    return [.. requests];
                }
            }
        }

        public Task<CompleteCheckoutResult> CompleteAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<CheckoutConfirmationResult> ConfirmAsync(CheckoutConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            lock (requests)
            {
                requests.Add(request);
            }

            Called.TrySetResult();
            if (gate is not null)
            {
                await gate;
            }

            return confirm(request);
        }
    }
}
