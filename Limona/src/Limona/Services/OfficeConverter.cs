using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Limona.Interop;

namespace Limona.Services;

/// <summary>
/// Converts Microsoft Office documents (Word, Excel, PowerPoint) and text files
/// to PDF, DOCX, TXT, HTML, EPUB, and RTF.
/// Multi-tier architecture:
///   1. Microsoft Office COM Automation (when Office is installed)
///   2. LibreOffice headless CLI (when LibreOffice is installed)
///   3. Universal Windows Standalone Fallback (OpenXML + built-in Microsoft Edge headless PDF engine)
/// Works on ANY Windows 10/11 machine, even with zero Microsoft Office installed.
/// </summary>
public static class OfficeConverter
{
    private static readonly HashSet<string> s_wordExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".doc", ".docm", ".dotx", ".dotm", ".rtf", ".txt", ".odt", ".htm", ".html"
    };

    private static readonly HashSet<string> s_excelExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xlsx", ".xls", ".xlsm", ".xlsb", ".xltx", ".csv", ".tsv"
    };

    private static readonly HashSet<string> s_pptExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pptx", ".ppt", ".pptm", ".ppsx", ".potx", ".odp"
    };

    private static readonly HashSet<string> s_allOfficeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".doc", ".docm", ".dotx", ".dotm", ".rtf", ".txt", ".odt", ".htm", ".html",
        ".xlsx", ".xls", ".xlsm", ".xlsb", ".xltx", ".csv", ".tsv",
        ".pptx", ".ppt", ".pptm", ".ppsx", ".potx", ".odp"
    };

    public static bool IsOfficeFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var ext = Path.GetExtension(path);
        return s_allOfficeExts.Contains(ext);
    }

    /// <summary>
    /// Converts an Office document to PDF. Backward-compatible wrapper around ConvertAsync.
    /// </summary>
    public static Task<string> ConvertToPdfAsync(string sourcePath) =>
        ConvertAsync(sourcePath, ".pdf");

    /// <summary>
    /// Converts a Word, Excel, or PowerPoint document to the target format (.pdf, .docx, .txt, .html, .epub, .rtf).
    /// Runs on a dedicated STA thread for COM stability when Office is present,
    /// or uses standalone fallbacks when Office is absent.
    /// Returns the path to the newly created file.
    /// </summary>
    public static Task<string> ConvertAsync(string sourcePath, string targetExt)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source file does not exist", sourcePath);

        targetExt = targetExt.Trim().ToLowerInvariant();
        if (!targetExt.StartsWith('.')) targetExt = "." + targetExt;

        var tcs = new TaskCompletionSource<string>();
        var thread = new Thread(() =>
        {
            try
            {
                var result = ConvertInternal(sourcePath, targetExt);
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                Dbg.Log($"OfficeConverter failed for '{sourcePath}' to '{targetExt}': {ex}");
                tcs.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }

    private static string ConvertInternal(string sourcePath, string targetExt)
    {
        var srcExt = Path.GetExtension(sourcePath).ToLowerInvariant();
        var dir = Path.GetDirectoryName(sourcePath)
                  ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var destPath = ImageConverter.GetUniqueOutputPath(dir, baseName, targetExt);

        if (s_wordExts.Contains(srcExt))
        {
            ConvertWord(sourcePath, destPath, targetExt, baseName);
        }
        else if (s_excelExts.Contains(srcExt))
        {
            ConvertExcel(sourcePath, destPath, targetExt, baseName);
        }
        else if (s_pptExts.Contains(srcExt))
        {
            ConvertPowerPoint(sourcePath, destPath, targetExt, baseName);
        }
        else
        {
            ConvertWord(sourcePath, destPath, targetExt, baseName);
        }

        if (!File.Exists(destPath) || new FileInfo(destPath).Length == 0)
            throw new FileNotFoundException($"Conversion to {targetExt} completed but output file was empty or missing", destPath);

        Dbg.Log($"OfficeConverter successfully converted '{sourcePath}' -> '{destPath}'");
        return destPath;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Word conversion router
    // ─────────────────────────────────────────────────────────────────────────

    private static void ConvertWord(string sourcePath, string destPath, string targetExt, string title)
    {
        // 1. Try native Microsoft Word COM if installed
        if (IsAppInstalled("Word.Application"))
        {
            try
            {
                ConvertWordViaCom(sourcePath, destPath, targetExt, title);
                if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
                    return;
            }
            catch (Exception ex)
            {
                Dbg.Log($"Word COM failed, falling back to standalone: {ex.Message}");
            }
        }

        // 2. Try LibreOffice CLI if installed
        if (TryConvertViaLibreOffice(sourcePath, destPath, targetExt))
            return;

        // 3. Standalone fallback: Pure C# + Microsoft Edge headless PDF engine
        Dbg.Log($"Using standalone fallback for Word conversion to {targetExt}");
        ConvertWordStandalone(sourcePath, destPath, targetExt, title);
    }

    private static void ConvertWordViaCom(string sourcePath, string destPath, string targetExt, string title)
    {
        var wordType = Type.GetTypeFromProgID("Word.Application")
                       ?? throw new InvalidOperationException("Microsoft Word COM unavailable.");

        dynamic word = Activator.CreateInstance(wordType)
                       ?? throw new InvalidOperationException("Could not instantiate Word.Application.");

        try
        {
            word.Visible = false;
            word.DisplayAlerts = 0; // wdAlertsNone

            dynamic doc = word.Documents.Open(
                FileName: sourcePath,
                ConfirmConversions: false,
                ReadOnly: true,
                AddToRecentFiles: false,
                Visible: false);

            try
            {
                if (targetExt == ".epub")
                {
                    var tempHtml = Path.Combine(Path.GetTempPath(), $"epub_tmp_{Guid.NewGuid():N}.html");
                    try
                    {
                        doc.SaveAs2(FileName: tempHtml, FileFormat: 10 /* wdFormatFilteredHTML */);
                        var html = File.Exists(tempHtml) ? File.ReadAllText(tempHtml) : "";
                        CreateEpub(destPath, title, html);
                    }
                    finally
                    {
                        if (File.Exists(tempHtml))
                            try { File.Delete(tempHtml); } catch { }
                    }
                }
                else
                {
                    int formatCode = targetExt switch
                    {
                        ".pdf"  => 17, // wdFormatPDF
                        ".docx" => 16, // wdFormatXMLDocument
                        ".txt"  => 2,  // wdFormatText
                        ".html" or ".htm" => 10, // wdFormatFilteredHTML
                        ".rtf"  => 6,  // wdFormatRTF
                        _       => 17,
                    };

                    doc.SaveAs2(FileName: destPath, FileFormat: formatCode);
                }
            }
            finally
            {
                try { doc.Close(SaveChanges: 0 /* wdDoNotSaveChanges */); } catch { }
                Marshal.FinalReleaseComObject(doc);
            }
        }
        finally
        {
            try { word.Quit(SaveChanges: 0 /* wdDoNotSaveChanges */); } catch { }
            Marshal.FinalReleaseComObject(word);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static void ConvertWordStandalone(string sourcePath, string destPath, string targetExt, string title)
    {
        var srcExt = Path.GetExtension(sourcePath).ToLowerInvariant();

        switch (targetExt)
        {
            case ".txt":
            {
                var text = ExtractTextFromDocument(sourcePath, srcExt);
                File.WriteAllText(destPath, text, Encoding.UTF8);
                break;
            }

            case ".html":
            case ".htm":
            {
                var html = DocumentToHtml(sourcePath, srcExt, title);
                File.WriteAllText(destPath, html, Encoding.UTF8);
                break;
            }

            case ".epub":
            {
                var html = DocumentToHtml(sourcePath, srcExt, title);
                CreateEpub(destPath, title, html);
                break;
            }

            case ".rtf":
            {
                var text = ExtractTextFromDocument(sourcePath, srcExt);
                var rtf = TextToRtf(text);
                File.WriteAllText(destPath, rtf, Encoding.ASCII);
                break;
            }

            case ".docx":
            {
                var text = ExtractTextFromDocument(sourcePath, srcExt);
                CreateDocxFromText(destPath, text);
                break;
            }

            case ".pdf":
            default:
            {
                // Convert to HTML, then render to PDF via Microsoft Edge headless
                var tempHtml = Path.Combine(Path.GetTempPath(), $"pdf_tmp_{Guid.NewGuid():N}.html");
                try
                {
                    var html = DocumentToHtml(sourcePath, srcExt, title);
                    File.WriteAllText(tempHtml, html, Encoding.UTF8);

                    if (!ConvertHtmlToPdfViaBrowser(tempHtml, destPath))
                    {
                        throw new InvalidOperationException(
                            "PDF generation failed: neither Microsoft Word nor a supported browser engine was available.");
                    }
                }
                finally
                {
                    if (File.Exists(tempHtml))
                        try { File.Delete(tempHtml); } catch { }
                }
                break;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Excel conversion router
    // ─────────────────────────────────────────────────────────────────────────

    private static void ConvertExcel(string sourcePath, string destPath, string targetExt, string title)
    {
        if (IsAppInstalled("Excel.Application"))
        {
            try
            {
                ConvertExcelViaCom(sourcePath, destPath, targetExt);
                if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
                    return;
            }
            catch (Exception ex)
            {
                Dbg.Log($"Excel COM failed, falling back to standalone: {ex.Message}");
            }
        }

        if (TryConvertViaLibreOffice(sourcePath, destPath, targetExt))
            return;

        // Standalone fallback
        Dbg.Log($"Using standalone fallback for Excel conversion to {targetExt}");
        ConvertExcelStandalone(sourcePath, destPath, targetExt, title);
    }

    private static void ConvertExcelViaCom(string sourcePath, string destPath, string targetExt)
    {
        var excelType = Type.GetTypeFromProgID("Excel.Application")
                        ?? throw new InvalidOperationException("Microsoft Excel COM unavailable.");

        dynamic excel = Activator.CreateInstance(excelType)
                        ?? throw new InvalidOperationException("Could not instantiate Excel.Application.");

        try
        {
            excel.Visible = false;
            excel.DisplayAlerts = false;

            dynamic workbook = excel.Workbooks.Open(
                Filename: sourcePath,
                UpdateLinks: 0,
                ReadOnly: true);

            try
            {
                if (targetExt == ".pdf")
                {
                    workbook.ExportAsFixedFormat(
                        Type: 0 /* xlTypePDF */,
                        Filename: destPath,
                        Quality: 0 /* xlQualityStandard */,
                        IncludeDocProperties: true,
                        IgnorePrintAreas: false,
                        OpenAfterPublish: false);
                }
                else
                {
                    int formatCode = targetExt switch
                    {
                        ".xlsx" => 51,    // xlOpenXMLWorkbook
                        ".csv"  => 6,     // xlCSV
                        ".html" or ".htm" => 44, // xlHtml
                        ".txt"  => -4158, // xlCurrentPlatformText
                        _       => 51,
                    };

                    workbook.SaveAs(Filename: destPath, FileFormat: formatCode);
                }
            }
            finally
            {
                try { workbook.Close(SaveChanges: false); } catch { }
                Marshal.FinalReleaseComObject(workbook);
            }
        }
        finally
        {
            try { excel.Quit(); } catch { }
            Marshal.FinalReleaseComObject(excel);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static void ConvertExcelStandalone(string sourcePath, string destPath, string targetExt, string title)
    {
        var rows = ExtractRowsFromSpreadsheet(sourcePath);

        switch (targetExt)
        {
            case ".csv":
                WriteCsv(destPath, rows);
                break;

            case ".txt":
                WriteTsv(destPath, rows);
                break;

            case ".html":
            case ".htm":
                var html = SpreadsheetToHtml(rows, title);
                File.WriteAllText(destPath, html, Encoding.UTF8);
                break;

            case ".pdf":
            default:
            {
                var tempHtml = Path.Combine(Path.GetTempPath(), $"excel_pdf_{Guid.NewGuid():N}.html");
                try
                {
                    var sheetHtml = SpreadsheetToHtml(rows, title);
                    File.WriteAllText(tempHtml, sheetHtml, Encoding.UTF8);
                    if (!ConvertHtmlToPdfViaBrowser(tempHtml, destPath))
                        throw new InvalidOperationException("PDF generation failed for spreadsheet.");
                }
                finally
                {
                    if (File.Exists(tempHtml))
                        try { File.Delete(tempHtml); } catch { }
                }
                break;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PowerPoint conversion router
    // ─────────────────────────────────────────────────────────────────────────

    private static void ConvertPowerPoint(string sourcePath, string destPath, string targetExt, string title)
    {
        if (IsAppInstalled("PowerPoint.Application"))
        {
            try
            {
                ConvertPowerPointViaCom(sourcePath, destPath, targetExt);
                if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
                    return;
            }
            catch (Exception ex)
            {
                Dbg.Log($"PowerPoint COM failed, falling back to standalone: {ex.Message}");
            }
        }

        if (TryConvertViaLibreOffice(sourcePath, destPath, targetExt))
            return;

        // Standalone fallback
        Dbg.Log($"Using standalone fallback for PowerPoint conversion to {targetExt}");
        ConvertPowerPointStandalone(sourcePath, destPath, targetExt, title);
    }

    private static void ConvertPowerPointViaCom(string sourcePath, string destPath, string targetExt)
    {
        var pptType = Type.GetTypeFromProgID("PowerPoint.Application")
                      ?? throw new InvalidOperationException("Microsoft PowerPoint COM unavailable.");

        dynamic ppt = Activator.CreateInstance(pptType)
                      ?? throw new InvalidOperationException("Could not instantiate PowerPoint.Application.");

        try
        {
            dynamic pres = ppt.Presentations.Open(
                FileName: sourcePath,
                ReadOnly: 1 /* msoTrue */,
                Untitled: 0 /* msoFalse */,
                WithWindow: 0 /* msoFalse */);

            try
            {
                int formatCode = targetExt switch
                {
                    ".pdf"  => 32, // ppSaveAsPDF
                    ".pptx" => 11, // ppSaveAsOpenXMLPresentation
                    ".rtf"  => 6,  // ppSaveAsRTF
                    _       => 32,
                };

                pres.SaveAs(destPath, formatCode);
            }
            finally
            {
                try { pres.Close(); } catch { }
                Marshal.FinalReleaseComObject(pres);
            }
        }
        finally
        {
            try { ppt.Quit(); } catch { }
            Marshal.FinalReleaseComObject(ppt);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static void ConvertPowerPointStandalone(string sourcePath, string destPath, string targetExt, string title)
    {
        var slides = ExtractSlidesFromPptx(sourcePath);

        switch (targetExt)
        {
            case ".txt":
                File.WriteAllText(destPath, string.Join("\r\n\r\n", slides), Encoding.UTF8);
                break;

            case ".html":
            case ".htm":
                var html = SlidesToHtml(slides, title);
                File.WriteAllText(destPath, html, Encoding.UTF8);
                break;

            case ".pdf":
            default:
            {
                var tempHtml = Path.Combine(Path.GetTempPath(), $"ppt_pdf_{Guid.NewGuid():N}.html");
                try
                {
                    var slidesHtml = SlidesToHtml(slides, title);
                    File.WriteAllText(tempHtml, slidesHtml, Encoding.UTF8);
                    if (!ConvertHtmlToPdfViaBrowser(tempHtml, destPath))
                        throw new InvalidOperationException("PDF generation failed for presentation.");
                }
                finally
                {
                    if (File.Exists(tempHtml))
                        try { File.Delete(tempHtml); } catch { }
                }
                break;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LibreOffice CLI Helper
    // ─────────────────────────────────────────────────────────────────────────

    private static bool TryConvertViaLibreOffice(string sourcePath, string destPath, string targetExt)
    {
        var soffice = FindLibreOfficeExecutable();
        if (soffice == null) return false;

        try
        {
            var outDir = Path.GetDirectoryName(destPath)!;
            var format = targetExt.TrimStart('.');
            var psi = new ProcessStartInfo
            {
                FileName = soffice,
                Arguments = $"--headless --convert-to {format} --outdir \"{outDir}\" \"{sourcePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var p = Process.Start(psi);
            if (p != null && p.WaitForExit(45000) && p.ExitCode == 0)
            {
                var baseName = Path.GetFileNameWithoutExtension(sourcePath);
                var defaultOut = Path.Combine(outDir, baseName + targetExt);
                if (File.Exists(defaultOut))
                {
                    if (!string.Equals(defaultOut, destPath, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(defaultOut, destPath, true);
                        try { File.Delete(defaultOut); } catch { }
                    }
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Dbg.Log($"LibreOffice CLI conversion failed: {ex.Message}");
        }

        return false;
    }

    private static string? FindLibreOfficeExecutable()
    {
        var candidates = new[]
        {
            @"C:\Program Files\LibreOffice\program\soffice.exe",
            @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
        };

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        return TryFindOnPath("soffice.exe");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Browser Engine Headless Print-to-PDF (Edge / Chrome)
    // ─────────────────────────────────────────────────────────────────────────

    private static bool ConvertHtmlToPdfViaBrowser(string htmlPath, string pdfPath)
    {
        var browser = FindBrowserExecutable();
        if (browser == null) return false;

        try
        {
            if (File.Exists(pdfPath))
                try { File.Delete(pdfPath); } catch { }

            var psi = new ProcessStartInfo
            {
                FileName = browser,
                Arguments = $"--headless --disable-gpu --run-all-compositor-stages-before-draw --print-to-pdf=\"{pdfPath}\" \"{htmlPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                if (process.WaitForExit(35000))
                {
                    return File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0;
                }
                else
                {
                    try { process.Kill(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Dbg.Log($"ConvertHtmlToPdfViaBrowser error: {ex.Message}");
        }

        return false;
    }

    private static string? FindBrowserExecutable()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe"),
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
        };

        foreach (var path in candidates)
            if (File.Exists(path)) return path;

        return TryFindOnPath("msedge.exe") ?? TryFindOnPath("chrome.exe");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Standalone OpenXML Parsers (No Office required)
    // ─────────────────────────────────────────────────────────────────────────

    private static string ExtractTextFromDocument(string sourcePath, string ext)
    {
        if (ext is ".txt")
            return File.ReadAllText(sourcePath);

        if (ext is ".rtf")
            return StripRtf(File.ReadAllText(sourcePath));

        if (ext is ".docx" or ".docm")
        {
            try
            {
                using var archive = ZipFile.OpenRead(sourcePath);
                var entry = archive.GetEntry("word/document.xml");
                if (entry != null)
                {
                    using var stream = entry.Open();
                    var xdoc = XDocument.Load(stream);
                    XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

                    var sb = new StringBuilder();
                    var paras = xdoc.Descendants(w + "p");
                    foreach (var p in paras)
                    {
                        var text = string.Concat(p.Descendants(w + "t").Select(t => t.Value));
                        if (!string.IsNullOrWhiteSpace(text))
                            sb.AppendLine(text);
                    }
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                Dbg.Log($"ExtractTextFromDocument error: {ex.Message}");
            }
        }

        return File.Exists(sourcePath) ? File.ReadAllText(sourcePath) : "";
    }

    private static string DocumentToHtml(string sourcePath, string ext, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\" />");
        sb.AppendLine($"<title>{System.Net.WebUtility.HtmlEncode(title)}</title>");
        sb.AppendLine("""
            <style>
              @page { margin: 1in; }
              body {
                font-family: 'Segoe UI', Calibri, Arial, sans-serif;
                font-size: 11pt;
                line-height: 1.6;
                color: #1e293b;
                max-width: 820px;
                margin: 0 auto;
                padding: 32px 24px;
                background: #ffffff;
              }
              p { margin: 0 0 10pt 0; }
              h1, h2, h3, h4 { color: #0f172a; margin-top: 18pt; margin-bottom: 8pt; font-weight: 600; }
              h1 { font-size: 20pt; border-bottom: 1px solid #e2e8f0; padding-bottom: 6pt; }
              h2 { font-size: 15pt; }
              h3 { font-size: 13pt; }
              table { border-collapse: collapse; width: 100%; margin: 14pt 0; }
              th, td { border: 1px solid #cbd5e1; padding: 6pt 10pt; text-align: left; }
              th { background: #f8fafc; font-weight: 600; }
            </style>
            </head>\n<body>
            """);

        if (ext is ".docx" or ".docm")
        {
            try
            {
                using var archive = ZipFile.OpenRead(sourcePath);
                var entry = archive.GetEntry("word/document.xml");
                if (entry != null)
                {
                    using var stream = entry.Open();
                    var xdoc = XDocument.Load(stream);
                    XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

                    var body = xdoc.Root?.Element(w + "body");
                    if (body != null)
                    {
                        foreach (var el in body.Elements())
                        {
                            if (el.Name == w + "p")
                            {
                                var pStyle = el.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value ?? "";
                                var tag = pStyle.StartsWith("Heading1", StringComparison.OrdinalIgnoreCase) ? "h1"
                                        : pStyle.StartsWith("Heading2", StringComparison.OrdinalIgnoreCase) ? "h2"
                                        : pStyle.StartsWith("Heading3", StringComparison.OrdinalIgnoreCase) ? "h3"
                                        : "p";

                                var runSb = new StringBuilder();
                                foreach (var run in el.Elements(w + "r"))
                                {
                                    var rPr = run.Element(w + "rPr");
                                    var bold = rPr?.Element(w + "b") != null;
                                    var italic = rPr?.Element(w + "i") != null;
                                    var underline = rPr?.Element(w + "u") != null;

                                    var text = string.Concat(run.Elements(w + "t").Select(t => t.Value));
                                    if (string.IsNullOrEmpty(text)) continue;

                                    var enc = System.Net.WebUtility.HtmlEncode(text);
                                    if (bold) enc = $"<b>{enc}</b>";
                                    if (italic) enc = $"<i>{enc}</i>";
                                    if (underline) enc = $"<u>{enc}</u>";

                                    runSb.Append(enc);
                                }

                                var content = runSb.ToString();
                                if (!string.IsNullOrWhiteSpace(content))
                                    sb.AppendLine($"<{tag}>{content}</{tag}>");
                            }
                            else if (el.Name == w + "tbl")
                            {
                                sb.AppendLine("<table>");
                                foreach (var row in el.Elements(w + "tr"))
                                {
                                    sb.AppendLine("  <tr>");
                                    foreach (var cell in row.Elements(w + "tc"))
                                    {
                                        var cellText = string.Concat(cell.Descendants(w + "t").Select(t => t.Value));
                                        sb.AppendLine($"    <td>{System.Net.WebUtility.HtmlEncode(cellText)}</td>");
                                    }
                                    sb.AppendLine("  </tr>");
                                }
                                sb.AppendLine("</table>");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Dbg.Log($"DocumentToHtml docx error: {ex.Message}");
            }
        }
        else
        {
            var raw = ExtractTextFromDocument(sourcePath, ext);
            foreach (var line in raw.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
            {
                if (string.IsNullOrWhiteSpace(line))
                    sb.AppendLine("<p>&nbsp;</p>");
                else
                    sb.AppendLine($"<p>{System.Net.WebUtility.HtmlEncode(line)}</p>");
            }
        }

        sb.AppendLine("</body>\n</html>");
        return sb.ToString();
    }

    private static List<List<string>> ExtractRowsFromSpreadsheet(string sourcePath)
    {
        var result = new List<List<string>>();
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();

        if (ext is ".csv")
        {
            foreach (var line in File.ReadAllLines(sourcePath))
                result.Add(line.Split(',').ToList());
            return result;
        }

        if (ext is ".xlsx")
        {
            try
            {
                using var zip = ZipFile.OpenRead(sourcePath);

                // Read shared strings
                var sharedStrings = new List<string>();
                var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
                if (ssEntry != null)
                {
                    using var stream = ssEntry.Open();
                    var xss = XDocument.Load(stream);
                    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                    foreach (var si in xss.Descendants(ns + "si"))
                    {
                        var text = string.Concat(si.Descendants(ns + "t").Select(t => t.Value));
                        sharedStrings.Add(text);
                    }
                }

                // Read first worksheet
                var sheetEntry = zip.GetEntry("xl/worksheets/sheet1.xml");
                if (sheetEntry != null)
                {
                    using var stream = sheetEntry.Open();
                    var xsheet = XDocument.Load(stream);
                    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

                    foreach (var row in xsheet.Descendants(ns + "row"))
                    {
                        var rowList = new List<string>();
                        foreach (var cell in row.Elements(ns + "c"))
                        {
                            var type = cell.Attribute("t")?.Value ?? "";
                            var val = cell.Element(ns + "v")?.Value ?? "";
                            if (type == "s" && int.TryParse(val, out var idx) && idx >= 0 && idx < sharedStrings.Count)
                                val = sharedStrings[idx];

                            rowList.Add(val);
                        }
                        result.Add(rowList);
                    }
                }
            }
            catch (Exception ex)
            {
                Dbg.Log($"ExtractRowsFromSpreadsheet error: {ex.Message}");
            }
        }

        return result;
    }

    private static string SpreadsheetToHtml(List<List<string>> rows, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\" />");
        sb.AppendLine($"<title>{System.Net.WebUtility.HtmlEncode(title)}</title>");
        sb.AppendLine("""
            <style>
              body { font-family: 'Segoe UI', Calibri, Arial, sans-serif; padding: 24px; color: #1e293b; }
              table { border-collapse: collapse; width: 100%; font-size: 10pt; }
              th, td { border: 1px solid #cbd5e1; padding: 5pt 8pt; text-align: left; }
              tr:first-child td { background: #f1f5f9; font-weight: 600; }
            </style>
            </head><body><table>
            """);

        foreach (var row in rows)
        {
            sb.AppendLine("  <tr>");
            foreach (var cell in row)
                sb.AppendLine($"    <td>{System.Net.WebUtility.HtmlEncode(cell)}</td>");
            sb.AppendLine("  </tr>");
        }

        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }

    private static List<string> ExtractSlidesFromPptx(string sourcePath)
    {
        var slides = new List<string>();
        try
        {
            using var zip = ZipFile.OpenRead(sourcePath);
            var slideEntries = zip.Entries
                .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml"))
                .OrderBy(e => e.FullName)
                .ToList();

            foreach (var entry in slideEntries)
            {
                using var stream = entry.Open();
                var xdoc = XDocument.Load(stream);
                XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
                var texts = xdoc.Descendants(a + "t").Select(t => t.Value).Where(t => !string.IsNullOrWhiteSpace(t));
                var slideContent = string.Join("\n", texts);
                if (!string.IsNullOrWhiteSpace(slideContent))
                    slides.Add(slideContent);
            }
        }
        catch (Exception ex)
        {
            Dbg.Log($"ExtractSlidesFromPptx error: {ex.Message}");
        }

        return slides;
    }

    private static string SlidesToHtml(List<string> slides, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\" />");
        sb.AppendLine($"<title>{System.Net.WebUtility.HtmlEncode(title)}</title>");
        sb.AppendLine("""
            <style>
              @page { size: landscape; margin: 0.5in; }
              body { font-family: 'Segoe UI', Arial, sans-serif; margin: 0; padding: 20px; }
              .slide { border: 1px solid #cbd5e1; border-radius: 8px; padding: 24px; margin-bottom: 24px; min-height: 400px; page-break-after: always; background: #ffffff; }
              h2 { color: #0f172a; margin-top: 0; }
              p { font-size: 13pt; line-height: 1.6; color: #334155; }
            </style>
            </head><body>
            """);

        for (var i = 0; i < slides.Count; i++)
        {
            sb.AppendLine($"<div class=\"slide\"><h2>Slide {i + 1}</h2>");
            foreach (var line in slides[i].Split('\n'))
                if (!string.IsNullOrWhiteSpace(line))
                    sb.AppendLine($"<p>{System.Net.WebUtility.HtmlEncode(line)}</p>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static void WriteCsv(string destPath, List<List<string>> rows)
    {
        using var sw = new StreamWriter(destPath, false, Encoding.UTF8);
        foreach (var row in rows)
        {
            var escaped = row.Select(c => c.Contains(',') || c.Contains('"') || c.Contains('\n')
                ? $"\"{c.Replace("\"", "\"\"")}\""
                : c);
            sw.WriteLine(string.Join(",", escaped));
        }
    }

    private static void WriteTsv(string destPath, List<List<string>> rows)
    {
        using var sw = new StreamWriter(destPath, false, Encoding.UTF8);
        foreach (var row in rows)
            sw.WriteLine(string.Join("\t", row));
    }

    private static void CreateDocxFromText(string destPath, string text)
    {
        if (File.Exists(destPath)) File.Delete(destPath);

        using var zip = new ZipArchive(File.Create(destPath), ZipArchiveMode.Create);

        // [Content_Types].xml
        var ctEntry = zip.CreateEntry("[Content_Types].xml");
        using (var w = new StreamWriter(ctEntry.Open(), Encoding.UTF8))
        {
            w.Write("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """);
        }

        // _rels/.rels
        var relsEntry = zip.CreateEntry("_rels/.rels");
        using (var w = new StreamWriter(relsEntry.Open(), Encoding.UTF8))
        {
            w.Write("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
        }

        // word/document.xml
        var docEntry = zip.CreateEntry("word/document.xml");
        using (var w = new StreamWriter(docEntry.Open(), Encoding.UTF8))
        {
            var sb = new StringBuilder();
            sb.Append("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body>
                """);

            foreach (var line in text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
            {
                var encoded = System.Security.SecurityElement.Escape(line);
                sb.Append($"<w:p><w:r><w:t>{encoded}</w:t></w:r></w:p>");
            }

            sb.Append("""
                  </w:body>
                </w:document>
                """);

            w.Write(sb.ToString());
        }
    }

    private static string TextToRtf(string text)
    {
        var sb = new StringBuilder();
        sb.Append(@"{\rtf1\ansi\deff0 {\fonttbl {\f0 Segoe UI;}} \f0\fs22 ");
        foreach (var line in text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
        {
            var escaped = line.Replace(@"\", @"\\").Replace("{", @"\{").Replace("}", @"\}");
            sb.Append(escaped);
            sb.Append(@"\par ");
        }
        sb.Append("}");
        return sb.ToString();
    }

    private static string StripRtf(string rtf)
    {
        if (string.IsNullOrEmpty(rtf) || !rtf.StartsWith("{\\rtf", StringComparison.OrdinalIgnoreCase))
            return rtf;

        var sb = new StringBuilder();
        var inControl = false;
        var depth = 0;

        for (var i = 0; i < rtf.Length; i++)
        {
            var c = rtf[i];
            if (c == '{') { depth++; continue; }
            if (c == '}') { depth--; continue; }
            if (depth > 1) continue; // skip header/font tables

            if (c == '\\')
            {
                inControl = true;
                continue;
            }

            if (inControl)
            {
                if (char.IsWhiteSpace(c) || c == ';') inControl = false;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    private static void CreateEpub(string epubPath, string title, string rawHtml)
    {
        if (File.Exists(epubPath)) File.Delete(epubPath);

        var bodyContent = rawHtml;
        var bodyStart = bodyContent.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyStart >= 0)
        {
            var tagEnd = bodyContent.IndexOf('>', bodyStart);
            if (tagEnd >= 0)
            {
                var bodyClose = bodyContent.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (bodyClose > tagEnd)
                    bodyContent = bodyContent.Substring(tagEnd + 1, bodyClose - tagEnd - 1);
            }
        }

        using var zip = new ZipArchive(File.Create(epubPath), ZipArchiveMode.Create);

        // 1. mimetype (must be first, uncompressed)
        var mimeEntry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var writer = new StreamWriter(mimeEntry.Open(), Encoding.ASCII))
        {
            writer.Write("application/epub+zip");
        }

        // 2. META-INF/container.xml
        var containerEntry = zip.CreateEntry("META-INF/container.xml");
        using (var writer = new StreamWriter(containerEntry.Open(), Encoding.UTF8))
        {
            writer.Write("""
                <?xml version="1.0" encoding="UTF-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles>
                    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
                  </rootfiles>
                </container>
                """);
        }

        // 3. OEBPS/content.opf
        var opfEntry = zip.CreateEntry("OEBPS/content.opf");
        using (var writer = new StreamWriter(opfEntry.Open(), Encoding.UTF8))
        {
            writer.Write($"""
                <?xml version="1.0" encoding="UTF-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="pub-id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:identifier id="pub-id">urn:uuid:{Guid.NewGuid()}</dc:identifier>
                    <dc:title>{System.Net.WebUtility.HtmlEncode(title)}</dc:title>
                    <dc:language>en</dc:language>
                  </metadata>
                  <manifest>
                    <item id="content" href="content.xhtml" media-type="application/xhtml+xml"/>
                  </manifest>
                  <spine>
                    <itemref idref="content"/>
                  </spine>
                </package>
                """);
        }

        // 4. OEBPS/content.xhtml
        var xhtmlEntry = zip.CreateEntry("OEBPS/content.xhtml");
        using (var writer = new StreamWriter(xhtmlEntry.Open(), Encoding.UTF8))
        {
            var encodedTitle = System.Net.WebUtility.HtmlEncode(title);
            writer.Write("<!DOCTYPE html>\n<html xmlns=\"http://www.w3.org/1999/xhtml\">\n<head>\n  <meta charset=\"utf-8\" />\n  <title>"
                         + encodedTitle
                         + "</title>\n  <style>body { font-family: sans-serif; line-height: 1.6; margin: 1em; }</style>\n</head>\n<body>\n"
                         + bodyContent
                         + "\n</body>\n</html>");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Utility helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static bool IsAppInstalled(string progId)
    {
        try
        {
            return Type.GetTypeFromProgID(progId) != null;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryFindOnPath(string exe)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }
}
