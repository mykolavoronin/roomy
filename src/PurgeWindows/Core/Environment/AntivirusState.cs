namespace PurgeWindows.Core.Environment;

/// <summary>
/// Coarse antivirus state decoding (W5 §5.2b). Deliberately coarse: the WMI productState bitmask
/// is undocumented, so only the two ranges W5 names are claimed, and anything else is Unknown.
/// The WSC API (primary source) reports state and signature status directly.
/// </summary>
public static class AntivirusState
{
    /// <summary>
    /// Decodes a WMI <c>AntiVirusProduct.productState</c> value. Enabled when the high byte is
    /// 0x06/0x07 (the 0x00060000–0x00070000 ranges, e.g. 397568/397312); disabled-or-snoozed when
    /// it is 0x04/0x05; anything else is Unknown. The WMI fallback never claims out-of-date:
    /// it cannot distinguish stale definitions, so it never says so.
    /// </summary>
    public static AvProductState DecodeProductState(uint productState)
    {
        var high = (productState >> 16) & 0xFF;
        return high switch
        {
            0x06 or 0x07 => AvProductState.Enabled,
            0x04 or 0x05 => AvProductState.DisabledOrSnoozed,
            _ => AvProductState.Unknown,
        };
    }

    // WSC_SECURITY_PRODUCT_STATE (iwscapi.h, verified against Microsoft Learn + the
    // Tailscale/Chromium WSC bindings): ON=0, OFF=1, SNOOZED=2, EXPIRED=3.
    // WSC_SECURITY_SIGNATURE_STATUS: OUT_OF_DATE=0, UP_TO_DATE=1.
    public const int WscStateOn = 0;
    public const int WscStateOff = 1;
    public const int WscStateSnoozed = 2;
    public const int WscStateExpired = 3;
    public const int WscSignatureOutOfDate = 0;
    public const int WscSignatureUpToDate = 1;

    /// <summary>
    /// Maps one WSC product's state + signature status to the coarse four-state decode. An
    /// expired product counts as disabled-or-snoozed (it is not protecting). An ON product
    /// whose signature status is unreadable still counts as enabled: a missing freshness
    /// reading must not demote a product Windows reports as on.
    /// </summary>
    public static (AvProductState State, AvSignatureStatus Signature) FromWsc(int state, int signatureStatus)
    {
        var signature = signatureStatus switch
        {
            WscSignatureUpToDate => AvSignatureStatus.UpToDate,
            WscSignatureOutOfDate => AvSignatureStatus.OutOfDate,
            _ => AvSignatureStatus.Unknown,
        };
        var product = state switch
        {
            WscStateOn => signature == AvSignatureStatus.OutOfDate ? AvProductState.OutOfDate : AvProductState.Enabled,
            WscStateOff or WscStateSnoozed or WscStateExpired => AvProductState.DisabledOrSnoozed,
            _ => AvProductState.Unknown,
        };
        return (product, signature);
    }

    /// <summary>
    /// True when a product is Microsoft Defender: its signed executable is MsMpEng.exe, or its
    /// display name says Defender. The executable path is the stronger signal; the name is the
    /// fallback for WSC rows, which carry no path.
    /// </summary>
    public static bool IsDefenderProduct(string? name, string? exePath)
    {
        if (!string.IsNullOrEmpty(exePath) &&
            exePath.EndsWith("MsMpEng.exe", StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrEmpty(name) &&
            name.Contains("Defender", StringComparison.OrdinalIgnoreCase);
    }
}
