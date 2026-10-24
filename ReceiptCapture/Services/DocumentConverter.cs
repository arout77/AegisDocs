using System.Diagnostics;
using System.Runtime.InteropServices;
using ReceiptCapture.Core.Redaction;

namespace ReceiptCapture.Services;

/// <summary>
/// Turns a Word document into a PDF using software already on this PC: Microsoft Word first,
/// LibreOffice as the fallback. Nothing is uploaded anywhere.
/// </summary>
public static class DocumentConverter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>Converts <paramref name="inputPath"/> and returns the path of the PDF written inside <paramref name="outputFolder"/>.</summary>
    public static async Task<string> ToPdfAsync(string inputPath, string outputFolder)
    {
        var name = Path.GetFileName(inputPath);

        if (InputFile.IsEncryptedDocx(inputPath))
            throw new InvalidOperationException(
                $"{name} is password-protected. In Word, remove the password (File > Info > Protect Document > Encrypt with Password), " +
                "save a copy, and open that copy here.");

        var target = Path.Combine(outputFolder, "converted.pdf");
        var problems = new List<string>();

        var work = Task.Run(() =>
        {
            if (TryWord(inputPath, target, problems) && File.Exists(target)) return true;
            if (TryLibreOffice(inputPath, outputFolder, target, problems) && File.Exists(target)) return true;
            return false;
        });

        if (await Task.WhenAny(work, Task.Delay(Timeout)) != work)
            throw new InvalidOperationException(
                $"Converting {name} took more than {Timeout.TotalMinutes:0} minutes, so it was given up on. " +
                "Word may still be running in the background; you can close it from Task Manager.");

        if (await work) return target;

        throw new InvalidOperationException(
            $"Couldn't turn {name} into a PDF.\n\n{string.Join("\n", problems)}\n\n" +
            "Opening Word documents here needs Microsoft Word (or LibreOffice) installed on this PC. " +
            "Or open the file in any program, save or print it as a PDF, and open that PDF here.");
    }

    // ------------------------------------------------------------------

    private static bool TryWord(string input, string target, List<string> problems)
    {
        var wordType = Type.GetTypeFromProgID("Word.Application");
        if (wordType == null)
        {
            problems.Add("Microsoft Word doesn't seem to be installed.");
            return false;
        }

        // Word is driven "late bound" (by name) so the app doesn't need Office files to build or run.
        dynamic? word = null;
        dynamic? document = null;
        try
        {
            word = Activator.CreateInstance(wordType);
            word.Visible = false;
            word.DisplayAlerts = 0;          // wdAlertsNone: never pop up a question nobody can see
            word.AutomationSecurity = 3;     // msoAutomationSecurityForceDisable: macros in the file don't run

            // A password is passed on purpose: for a protected file Word then fails straight away
            // instead of waiting for someone to type it into a window that isn't shown.
            document = word.Documents.Open(
                FileName: input,
                ConfirmConversions: false,
                ReadOnly: true,
                AddToRecentFiles: false,
                PasswordDocument: Guid.NewGuid().ToString("N"),
                Visible: false);

            document.ExportAsFixedFormat(
                OutputFileName: target,
                ExportFormat: 17,            // wdExportFormatPDF
                OpenAfterExport: false,
                OptimizeFor: 0,              // wdExportOptimizeForPrint
                Range: 0,                    // wdExportAllDocument
                Item: 0,                     // wdExportDocumentContent: the text itself, without tracked-change markup
                IncludeDocProps: false,
                KeepIRM: false,
                CreateBookmarks: 0,
                DocStructureTags: false,
                BitmapMissingFonts: true,
                UseISO19005_1: false);
            return true;
        }
        catch (Exception ex)
        {
            problems.Add($"Word couldn't convert it ({FirstLine(ex.Message)}). If the file is password-protected, remove the password first.");
            return false;
        }
        finally
        {
            try { document?.Close(false); } catch (Exception) { }
            try { word?.Quit(false); } catch (Exception) { }
            Release((object?)document);
            Release((object?)word);
        }
    }

    private static bool TryLibreOffice(string input, string outputFolder, string target, List<string> problems)
    {
        var soffice = FindLibreOffice();
        if (soffice == null)
        {
            problems.Add("LibreOffice doesn't seem to be installed either.");
            return false;
        }

        try
        {
            // A private profile folder keeps this run from clashing with a LibreOffice window that's already open.
            var profile = new Uri(Path.Combine(outputFolder, "lo-profile")).AbsoluteUri;
            var start = new ProcessStartInfo(soffice)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add($"-env:UserInstallation={profile}");
            start.ArgumentList.Add("--headless");
            start.ArgumentList.Add("--norestore");
            start.ArgumentList.Add("--convert-to");
            start.ArgumentList.Add("pdf");
            start.ArgumentList.Add("--outdir");
            start.ArgumentList.Add(outputFolder);
            start.ArgumentList.Add(input);

            using var process = Process.Start(start);
            if (process == null) { problems.Add("LibreOffice wouldn't start."); return false; }

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                problems.Add("LibreOffice took too long.");
                return false;
            }

            var produced = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(input) + ".pdf");
            if (!File.Exists(produced))
            {
                problems.Add("LibreOffice ran but didn't produce a PDF.");
                return false;
            }
            File.Move(produced, target, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            problems.Add($"LibreOffice couldn't convert it ({FirstLine(ex.Message)}).");
            return false;
        }
    }

    private static string? FindLibreOffice()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
        {
            var candidate = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static void Release(object? comObject)
    {
        if (comObject != null && Marshal.IsComObject(comObject))
        {
            try { Marshal.FinalReleaseComObject(comObject); } catch (Exception) { }
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', '\r')[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
