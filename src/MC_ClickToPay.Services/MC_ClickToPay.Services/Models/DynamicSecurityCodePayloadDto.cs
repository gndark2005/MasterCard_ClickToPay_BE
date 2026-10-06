namespace MC_ClickToPay.Services.Models;

/// <summary>
/// PAN (token only markets) payload: card + Dynamic Token Verification Code in dynamicData
/// (DYNAMIC_CARD_SECURITY_CODE), used in place of the CVC2.
/// </summary>
public sealed class DynamicSecurityCodePayloadDto : CardPayloadDto;
