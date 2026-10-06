namespace MC_ClickToPay.Services.Payments;

/// <summary>Which credential the decrypted Mastercard payload carried.</summary>
public enum PaymentCredentialType
{
    /// <summary>token.paymentToken + cryptogram (dynamicDataType CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM).</summary>
    NetworkToken,

    /// <summary>
    /// card.primaryAccountNumber: alone (NONE), with a DTVC (DYNAMIC_CARD_SECURITY_CODE) or with a DSRP cryptogram
    /// next to a masked token (DSRP + PAN).
    /// </summary>
    Pan
}
