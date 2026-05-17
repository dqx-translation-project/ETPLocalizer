namespace ETPLocalizer.Wii;

internal sealed record WiiRpsEntry(int SectionIndex, string SectionName, byte[] Evtx);

internal static class WiiRpsExtractor
{
    // Extract all ETP-typed XTVE sections from a Wii RPS. CRY/other section payloads are skipped silently;
    // none have been observed in Wii RPS files.
    public static List<WiiRpsEntry> Extract(byte[] rpsRaw)
    {
        var sed = new SedbresFile(rpsRaw);
        var entries = new List<WiiRpsEntry>();
        foreach (var sec in sed.ResourceSections)
        {
            if (sec.TypeTag != "etp") continue;
            if (sec.Data.Length < 4) continue;
            if (sec.Data[0] != 'X' || sec.Data[1] != 'T' || sec.Data[2] != 'V' || sec.Data[3] != 'E') continue;
            entries.Add(new WiiRpsEntry(sec.Index, sec.Name, sec.Data));
        }
        return entries;
    }

    // Rebuild the RPS replacing sections whose Index appears in replacements with new bytes.
    public static byte[] Rebuild(byte[] originalRps, IReadOnlyDictionary<int, byte[]> replacements) =>
        new SedbresFile(originalRps).Rebuild(replacements);
}
