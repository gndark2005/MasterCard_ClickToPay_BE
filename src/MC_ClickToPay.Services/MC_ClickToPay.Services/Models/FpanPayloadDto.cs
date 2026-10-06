namespace MC_ClickToPay.Services.Models;

/// <summary>FPAN payload: card only, no dynamic data (dynamicDataType NONE or absent).</summary>
public sealed class FpanPayloadDto : CardPayloadDto;
