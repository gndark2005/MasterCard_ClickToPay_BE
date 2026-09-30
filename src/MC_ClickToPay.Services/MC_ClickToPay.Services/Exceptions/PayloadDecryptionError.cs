namespace MC_ClickToPay.Services.Exceptions;

public enum PayloadDecryptionError
{
    InvalidInput,
    UnsupportedHeader,
    DecryptionFailed,
    InvalidPayload
}
