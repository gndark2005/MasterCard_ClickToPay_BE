namespace MC_ClickToPay.Services.Payments.PowerTranz;

/// <summary>PowerTranz expects ISO numeric codes; Mastercard sends ISO alphabetic ones.</summary>
internal static class PowerTranzCodes
{
    private static readonly Dictionary<string, string> Currencies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = "840", ["EUR"] = "978", ["GBP"] = "826", ["CAD"] = "124", ["MXN"] = "484", ["JMD"] = "388",
        ["TTD"] = "780", ["BBD"] = "052", ["BSD"] = "044", ["KYD"] = "136", ["XCD"] = "951", ["DOP"] = "214",
        ["COP"] = "170", ["BRL"] = "986", ["GTQ"] = "320", ["CRC"] = "188", ["PAB"] = "590", ["AWG"] = "533",
        ["ANG"] = "532", ["BZD"] = "084", ["GYD"] = "328", ["HTG"] = "332", ["BMD"] = "060",
    };

    private static readonly Dictionary<string, string> Countries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "840", ["CA"] = "124", ["MX"] = "484", ["GB"] = "826", ["ES"] = "724", ["FR"] = "250",
        ["DE"] = "276", ["IT"] = "380", ["NL"] = "528", ["IE"] = "372", ["PT"] = "620", ["BR"] = "076",
        ["AR"] = "032", ["CO"] = "170", ["CL"] = "152", ["PE"] = "604", ["JM"] = "388", ["TT"] = "780",
        ["BB"] = "052", ["BS"] = "044", ["KY"] = "136", ["DO"] = "214", ["PR"] = "630", ["PA"] = "591",
        ["CR"] = "188", ["GT"] = "320", ["AE"] = "784", ["PH"] = "608", ["VN"] = "704", ["KW"] = "414",
        ["AU"] = "036", ["IN"] = "356", ["AW"] = "533", ["CW"] = "531", ["BZ"] = "084", ["GY"] = "328",
        ["HT"] = "332", ["BM"] = "060", ["LC"] = "662", ["AG"] = "028", ["GD"] = "308", ["KN"] = "659",
        ["VC"] = "670", ["DM"] = "212",
    };

    /// <summary>Numeric codes pass through; unknown alphabetic codes throw (PowerTranz would reject them anyway).</summary>
    public static string Currency(string code)
    {
        var value = code.Trim();
        if (value.All(char.IsAsciiDigit))
        {
            return value.PadLeft(3, '0');
        }

        return Currencies.TryGetValue(value, out var numeric)
            ? numeric
            : throw new PaymentProcessingException($"Currency {value} has no PowerTranz numeric code configured.");
    }

    public static string? Country(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null
        : code.All(char.IsAsciiDigit) ? code
        : Countries.GetValueOrDefault(code.Trim(), code.Trim());
}
