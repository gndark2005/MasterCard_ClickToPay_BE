namespace MC_ClickToPay.Services.Payments;

public enum SimulatedOutcome
{
    /// <summary>HTTP 200, approved = true, responseCode "00".</summary>
    Approved,

    /// <summary>HTTP 200, approved = false, responseCode "05".</summary>
    Declined,

    /// <summary>HTTP 502: the processor could not give a result.</summary>
    Failure
}
