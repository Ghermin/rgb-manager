namespace RgbManager;

/// <summary>Fichiers embarqués dans l'exe par build.ps1.</summary>
static class Embedded
{
    /// <summary>Module SMBus PIIX4 signé de PawnIO (vendor\pawnio\SmbusPIIX4.bin, LGPL-2.1).</summary>
    public static byte[] SmbusPIIX4
    {
        get
        {
            using var stream = typeof(Embedded).Assembly.GetManifestResourceStream("SmbusPIIX4.bin")
                ?? throw new FileNotFoundException("Module SmbusPIIX4.bin absent de l'exe");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }
}
