namespace MC_ClickToPay.PaymentDemo.Api.Payments;

/// <summary>What a processor returns for an approval or a decline (failures throw <see cref="PaymentProcessingException"/>).</summary>
public sealed class PaymentResult
{
    public required bool Approved { get; init; }

    public required string TransactionId { get; init; }

    public string? AuthorizationCode { get; init; }

    /// <summary>ISO response code, as in PowerTranz IsoResponseCode: "00" is approved.</summary>
    public required string ResponseCode { get; init; }

    public required string ResponseMessage { get; init; }

    public required DateTimeOffset ProcessedAt { get; init; }
}
