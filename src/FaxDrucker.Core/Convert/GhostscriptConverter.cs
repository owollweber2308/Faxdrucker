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
    /// Erzeugt ein mehrseitiges Fax-TIFF (1728 Pixel breit = 215 mm bei 204 dpi) und gibt die Seitenzahl zurück.
    /// </summary>
    public async Task<int> ToFaxTiffAsync(string input, string outputTiff,
        FaxResolution resolution = FaxResolution.Fein, ImageMode mode = ImageMode.Text, CancellationToken ct = default)
    {
        int yRes = resolution.VerticalDpi();
        int height = (int)Math.Round(11.69 * yRes); // A4-Höhe
        var args = new List<string> { "-q", "-dNOPAUSE", "-dBATCH", "-dSAFER" };

        if (mode == ImageMode.Text)
        {
            // Direkt in Schwarz/Weiß rendern: schärfste Schrift, kleinste Datei.
            args.AddRange(new[]
            {
                "-sDEVICE=tiffg3", $"-r204x{yRes}", $"-g1728x{height}",
                "-dTextAlphaBits=1", "-dGraphicsAlphaBits=1",
            });
        }
        else
        {
            // Mit dreifacher Auflösung in Graustufen rendern und per Fehlerdiffusion
            // auf Schwarz/Weiß verkleinern: Fotos, Logos und Graustufen bleiben erkennbar.
            const int f = 3;
            args.AddRange(new[]
            {
                "-sDEVICE=tiffscaled", "-sCompression=g4", $"-dDownScaleFactor={f}",
                $"-r{204 * f}x{yRes * f}", $"-g{1728 * f}x{height * f}",
                "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4",
            });
        }
        args.AddRange(new[] { "-dFIXEDMEDIA", "-dPDFFitPage", "-dFitPage", "-sOutputFile=" + outputTiff, input });

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

/// <summary>Faxauflösung (horizontal immer 204 dpi).</summary>
public enum FaxResolution
{
    /// <summary>204×98 dpi – schnell, für einfache Texte.</summary>
    Normal,
    /// <summary>204×196 dpi – Standard für Briefe.</summary>
    Fein,
    /// <summary>204×391 dpi – kleine Schrift und Details; dauert etwa doppelt so lange wie Fein.</summary>
    Superfein,
}

/// <summary>Wie das Dokument in Schwarz/Weiß umgesetzt wird.</summary>
public enum ImageMode
{
    /// <summary>Scharfe Kanten, ideal für Text und Tabellen.</summary>
    Text,
    /// <summary>Fehlerdiffusion (Rasterung) für Fotos, Logos und Graustufen.</summary>
    Foto,
}

public static class FaxResolutionExtensions
{
    public static int VerticalDpi(this FaxResolution r) => r switch
    {
        FaxResolution.Normal => 98,
        FaxResolution.Superfein => 391,
        _ => 196,
    };

    public static string DisplayName(this FaxResolution r) => r switch
    {
        FaxResolution.Normal => "Normal (204×98 dpi)",
        FaxResolution.Superfein => "Superfein (204×391 dpi)",
        _ => "Fein (204×196 dpi)",
    };
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
