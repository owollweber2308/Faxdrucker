using System.Diagnostics;

namespace FaxDrucker.Core.Convert;

/// <summary>
/// Wandelt PostScript/PDF mit Ghostscript in ein Fax-TIFF (G3, 1728 Pixel breit) um.
/// </summary>
public sealed class GhostscriptConverter
{
    public string Executable { get; }

    public GhostscriptConverter(string? executable = null)
    {
        Executable = executable ?? Find() ?? throw new FileNotFoundException(
            "Ghostscript wurde nicht gefunden. Bitte Ghostscript (64 Bit) installieren: https://ghostscript.com/releases/gsdnld.html");
    }

    /// <summary>Sucht gswin64c.exe (Windows) bzw. gs (Linux).</summary>
    public static string? Find()
    {
        var env = Environment.GetEnvironmentVariable("FAXDRUCKER_GS");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        if (OperatingSystem.IsWindows())
        {
            var bases = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };
            foreach (var b in bases.Where(Directory.Exists))
            {
                var gsDir = Path.Combine(b, "gs");
                if (!Directory.Exists(gsDir)) continue;
                var hit = Directory.GetDirectories(gsDir)
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(d => new[] { Path.Combine(d, "bin", "gswin64c.exe"), Path.Combine(d, "bin", "gswin32c.exe") })
                    .FirstOrDefault(File.Exists);
                if (hit != null) return hit;
            }
            // Mitgelieferte Kopie neben dem Programm
            var local = Path.Combine(AppContext.BaseDirectory, "gs", "bin", "gswin64c.exe");
            return File.Exists(local) ? local : null;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var p = Path.Combine(dir, "gs");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// Erzeugt ein mehrseitiges TIFF. fine = 204x196 dpi (Fein), sonst 204x98 dpi (Normal).
    /// Gibt die Seitenzahl zurück.
    /// </summary>
    public async Task<int> ToFaxTiffAsync(string input, string outputTiff, bool fine = true, CancellationToken ct = default)
    {
        int yRes = fine ? 196 : 98;
        // A4 auf Faxbreite 1728 Pixel (215 mm) bei 204 dpi; Höhe A4 (297 mm).
        int height = (int)Math.Round(11.69 * yRes);
        var args = new List<string>
        {
            "-q", "-dNOPAUSE", "-dBATCH", "-dSAFER",
            "-sDEVICE=tiffg3",
            $"-r204x{yRes}",
            $"-g1728x{height}",
            "-dFIXEDMEDIA", "-dPDFFitPage", "-dFitPage",
            "-dTextAlphaBits=1", "-dGraphicsAlphaBits=1",
            "-sOutputFile=" + outputTiff,
            input,
        };

        var psi = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Ghostscript konnte nicht gestartet werden.");
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stderr = await stderrTask;
        await stdoutTask;
        if (proc.ExitCode != 0 || !File.Exists(outputTiff))
            throw new InvalidOperationException($"Ghostscript-Fehler ({proc.ExitCode}): {stderr.Trim()}");
        return TiffInfo.CountPages(outputTiff);
    }
}

/// <summary>Zählt die Seiten (IFDs) einer TIFF-Datei ohne externe Bibliothek.</summary>
public static class TiffInfo
{
    public static int CountPages(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        var order = br.ReadUInt16();
        bool le = order == 0x4949;
        if (!le && order != 0x4D4D) return 0;
        ushort U16() { var b = br.ReadBytes(2); return le ? BitConverter.ToUInt16(b) : (ushort)(b[0] << 8 | b[1]); }
        uint U32() { var b = br.ReadBytes(4); return le ? BitConverter.ToUInt32(b) : (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
        if (U16() != 42) return 0;
        uint offset = U32();
        int pages = 0;
        var seen = new HashSet<uint>();
        while (offset != 0 && offset < fs.Length && seen.Add(offset) && pages < 10000)
        {
            fs.Position = offset;
            var entries = U16();
            fs.Position = offset + 2 + entries * 12L;
            pages++;
            offset = U32();
        }
        return pages;
    }
}
