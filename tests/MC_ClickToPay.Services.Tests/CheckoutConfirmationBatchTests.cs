using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Checkout;
using MC_ClickToPay.Services.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MC_ClickToPay.Services.Tests;

public sealed class CheckoutConfirmationBatchTests
{
    [Fact]
    public async Task EachRowGetsItsOwnResultInFileOrder()
    {
        var checkout = new FakeCheckout(request => request.CorrelationId switch
        {
            "corr-missing" => throw new MastercardCheckoutException("[NOT_FOUND] Transaction data not found", 404),
            "corr-down" => throw new MastercardCheckoutException("Could not reach Mastercard."),
            _ => new CheckoutConfirmationResult { FlowId = $"flow-{request.CorrelationId}" }
        });
        CheckoutTransactionRow[] rows =
        [
            Row(2, "corr-ok"),
            Row(3, "corr-missing"),
            Row(4, "corr-pending", outcome: false),
            Row(5, "corr-bad", status: "OK"),
            Row(6, "corr-down"),
        ];

        var result = await Batch(checkout).ProcessAsync(rows, "ctp_txn_20261015.csv", CancellationToken.None);

        Assert.Equal("ctp_txn_20261015.csv", result.FileName);
        Assert.Equal(5, result.TotalRows);
        Assert.Equal((1, 2, 1, 1), (result.Confirmed, result.Failed, result.Invalid, result.Skipped));
        Assert.Equal([2, 3, 4, 5, 6], result.Results.Select(r => r.Line));
        Assert.Equal(
            [ConfirmationRowStatus.Confirmed, ConfirmationRowStatus.Failed, ConfirmationRowStatus.Skipped,
             ConfirmationRowStatus.Invalid, ConfirmationRowStatus.Failed],
            result.Results.Select(r => r.Status));
        Assert.Equal("flow-corr-ok", result.Results[0].FlowId);
        Assert.Equal(404, result.Results[1].HttpStatus);
        Assert.Contains("NOT_FOUND", result.Results[1].Detail);
        Assert.Contains("confirmation_status must be a two-digit code", result.Results[3].Detail);
        Assert.Equal(502, result.Results[4].HttpStatus);

        // Skipped and invalid rows are never sent (calls run concurrently, so compare as a set).
        Assert.Equal(["corr-down", "corr-missing", "corr-ok"],
            checkout.Requests.Select(r => r.CorrelationId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RowsAreSentWithTheSameFieldsAsASingleConfirmation()
    {
        var checkout = new FakeCheckout(_ => new CheckoutConfirmationResult());

        await Batch(checkout).ProcessAsync([Row(2, "corr-1")], null, CancellationToken.None);

        var sent = Assert.Single(checkout.Requests);
        Assert.Equal("corr-1", sent.CorrelationId);
        Assert.Equal("mtid-1", sent.MerchantTransactionId);
        Assert.Equal("flow-1", sent.FlowId);
        Assert.Equal("x-1", sent.XCorrelationId);
        Assert.Equal("01", sent.ConfirmationData!.CheckoutEventType);
        Assert.Equal("01", sent.ConfirmationData.CheckoutEventStatus);
        Assert.Equal("01", sent.ConfirmationData.ConfirmationStatus);
        Assert.Equal("The payment was successful", sent.ConfirmationData.ConfirmationReason);
        Assert.Equal("123456", sent.ConfirmationData.NetworkAuthorizationCode);
        Assert.Equal(31.25m, sent.ConfirmationData.TransactionAmount!.Amount);
        Assert.Equal("USD", sent.ConfirmationData.TransactionAmount.CurrencyCode);
        Assert.Null(sent.ConfirmationData.ConfirmationTimestamp);
    }

    [Theory]
    [InlineData("0", "USD")]
    [InlineData("12,50", "USD")]
    [InlineData("1.234", "USD")]
    [InlineData("10", "840")]
    [InlineData("10", null)]
    public async Task InvalidAmountsAreReportedWithoutCallingMastercard(string amount, string? currency)
    {
        var checkout = new FakeCheckout(_ => new CheckoutConfirmationResult());

        var result = await Batch(checkout).ProcessAsync(
            [Row(2, "corr-1", amount: amount, currency: currency)], null, CancellationToken.None);

        Assert.Equal(ConfirmationRowStatus.Invalid, Assert.Single(result.Results).Status);
        Assert.Empty(checkout.Requests);
    }

    [Fact]
    public async Task NonMastercardErrorsAbortTheBatch()
    {
        var checkout = new FakeCheckout(_ => throw new InvalidOperationException("signing key unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Batch(checkout).ProcessAsync([Row(2, "corr-1")], null, CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmedAndFailedRowsAreRecordedButInvalidAndSkippedAreNot()
    {
        var checkout = new FakeCheckout(request => request.CorrelationId == "corr-missing"
            ? throw new MastercardCheckoutException("[NOT_FOUND] Transaction data not found", 404)
            : new CheckoutConfirmationResult { FlowId = "flow-back" });
        var store = new RecordingStore();

        await Batch(checkout, store).ProcessAsync(
            [Row(2, "corr-ok"), Row(3, "corr-missing"), Row(4, "corr-pending", outcome: false), Row(5, "corr-bad", status: "x")],
            null, CancellationToken.None);

        var recorded = store.Confirmations.OrderBy(c => c.Request.CorrelationId, StringComparer.Ordinal).ToList();
        Assert.Equal(2, recorded.Count);
        Assert.Equal("corr-missing", recorded[0].Request.CorrelationId);
        Assert.False(recorded[0].Outcome.Confirmed);
        Assert.Equal(404, recorded[0].Outcome.HttpStatus);
        Assert.Equal("corr-ok", recorded[1].Request.CorrelationId);
        Assert.True(recorded[1].Outcome.Confirmed);
        Assert.Equal("flow-back", recorded[1].Outcome.FlowId);
    }

    [Fact]
    public async Task ADatabaseErrorDoesNotChangeTheRowResult()
    {
        var checkout = new FakeCheckout(_ => new CheckoutConfirmationResult());
        var store = new RecordingStore { Failure = new InvalidOperationException("database down") };

        var result = await Batch(checkout, store).ProcessAsync([Row(2, "corr-1")], null, CancellationToken.None);

        Assert.Equal(ConfirmationRowStatus.Confirmed, Assert.Single(result.Results).Status);
    }

    private static CheckoutConfirmationBatch Batch(FakeCheckout checkout, ICheckoutTransactionStore? store = null) =>
        new(checkout, new CheckoutStateRecorder(store ?? new NoCheckoutTransactionStore(), NullLogger<CheckoutStateRecorder>.Instance),
            NullLogger<CheckoutConfirmationBatch>.Instance);

    private sealed class RecordingStore : ICheckoutTransactionStore
    {
        public List<(CheckoutConfirmationRequest Request, ConfirmationOutcome Outcome)> Confirmations { get; } = [];

        public Exception? Failure { get; init; }

        public Task RecordCheckoutAsync(CompleteCheckoutRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordConfirmationAsync(CheckoutConfirmationRequest request, ConfirmationOutcome outcome,
            CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            lock (Confirmations)
            {
                Confirmations.Add((request, outcome));
            }

            return Task.CompletedTask;
        }
    }

    private static CheckoutTransactionRow Row(int line, string correlationId, bool outcome = true, string status = "01",
        string amount = "31.25", string? currency = "USD") => new()
    {
        Line = line,
        TransactionAmount = amount,
        TransactionCurrency = currency,
        SrcCorrelationId = correlationId,
        MerchantTransactionId = "mtid-1",
        FlowId = "flow-1",
        XCorrelationId = "x-1",
        CheckoutEventType = outcome ? "01" : null,
        CheckoutEventStatus = outcome ? "01" : null,
        ConfirmationStatus = outcome ? status : null,
        ConfirmationReason = outcome ? "The payment was successful" : null,
        NetworkAuthorizationCode = outcome ? "123456" : null
    };

    private sealed class FakeCheckout(Func<CheckoutConfirmationRequest, CheckoutConfirmationResult> confirm) : IMastercardCheckoutService
    {
        private readonly List<CheckoutConfirmationRequest> requests = [];

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

        public Task<CheckoutConfirmationResult> ConfirmAsync(CheckoutConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            lock (requests)
            {
                requests.Add(request);
            }

            return Task.FromResult(confirm(request));
        }
    }
}
