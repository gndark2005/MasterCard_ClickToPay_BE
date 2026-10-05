namespace PowerTranz3DSecurePoc;

internal static class SensitiveData
{
    /// <summary>Shows only the first/last 4 characters, e.g. "abcd…wxyz (36 chars)".</summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        if (value.Length <= 8) return new string('*', value.Length);
        return $"{value[..4]}…{value[^4..]} ({value.Length} chars)";
    }

    /// <summary>Card number as "5115 •••• 0001".</summary>
    public static string MaskPan(string? pan) =>
        string.IsNullOrEmpty(pan) || pan.Length < 8 ? "(empty)" : $"{pan[..4]} •••• {pan[^4..]}";

    /// <summary>Drops query strings, which may carry tokens.</summary>
    public static string SafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Query)
            ? uri.GetLeftPart(UriPartial.Path) + "?…"
            : url;
}
