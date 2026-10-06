namespace MC_ClickToPay.Services.Models;

/// <summary>Values of dynamicData.dynamicDataType: they decide which decrypted payload model is used.</summary>
public static class DynamicDataTypes
{
    /// <summary>DSRP cryptogram: <see cref="TokenizedPayloadDto"/>, or <see cref="DsrpPanPayloadDto"/> when a card is present.</summary>
    public const string Cryptogram = "CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM";

    /// <summary>Dynamic card security code (DTVC): <see cref="DynamicSecurityCodePayloadDto"/>.</summary>
    public const string DynamicSecurityCode = "DYNAMIC_CARD_SECURITY_CODE";

    /// <summary>No dynamic data, FPAN: <see cref="FpanPayloadDto"/>.</summary>
    public const string None = "NONE";
}
