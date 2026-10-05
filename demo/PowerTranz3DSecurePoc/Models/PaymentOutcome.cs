namespace PowerTranz3DSecurePoc.Models;

/// <summary>Cancelled = the shopper went back or switched payment method mid-attempt (no popup is shown).</summary>
public enum OutcomeKind { Approved, Declined, Error, Cancelled }

/// <summary>Result of one payment attempt, shown in the checkout page popup.</summary>
/// <param name="Exception">Exception text for errors (type and message; full details for unexpected ones).</param>
public sealed record PaymentOutcome(
    OutcomeKind Kind,
    string Title,
    string Message,
    IReadOnlyList<OutcomeDetail> Details,
    string? Exception = null);

public sealed record OutcomeDetail(string Label, string Value);
