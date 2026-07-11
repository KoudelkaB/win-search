using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;
using SharpCompress.Writers.Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace search.Models
{
    public static class ZipExtensions
    {
        /// <summary>
        /// Loads the entry into memory stream that is seekable/writable despice only readable stream from OpenEntryStream
        /// </summary>
        /// <param name="e"></param>
        /// <returns></returns>
        public static Stream Load(this IArchiveEntry e)
        {
            var mza = new MemoryStream();
            try
            {
                /*using*/
                var sza = e.OpenEntryStream(); //Do not close the stream for other readers!!!
                sza.CopyTo(mza);
                mza.Position = 0;
                return mza;
            }
            catch (Exception)
            {
                mza?.Dispose();
            }
            return null;
        }


        /// <summary>
        /// Get nested archive corresponding to given archive entry if any
        /// Does not throw Exception but return null instead
        /// </summary>
        /// <param name="e"></param>
        /// <returns>Archive that needs to be disposed - but independently of source archive</returns>
        public static IArchive ToArchive(this IArchiveEntry e)
        {
            if (e.Load() is Stream s)
            {
                try
                {
                    return ArchiveFactory.OpenArchive(s, new SharpCompress.Readers.ReaderOptions { ExtensionHint = e.Key });
                }
                catch
                {
                    s.Dispose();
                }
            }
            return null;
        }

        /// <summary>
        /// Returns first structured archive (cotaining file nodes e.g. tar)
        /// for unstructured archive (only data without any structure e.g. gz, bz2)
        /// Does not throw Exception.
        /// </summary>
        /// <param name="a">This archive is disposed in the function if not returned as a result</param>
        /// <returns></returns>
        public static IArchive StructuredSubArchive(this IArchive a)
        {
            var entries = a?.Entries.Take(2).ToArray();
            if (entries?.Length == 1 && entries[0].Key == null && entries[0].ToArchive() is IArchive sa)
            {
                //Dispose unstructured archive and get structured from sa
                using (a) return sa.StructuredSubArchive();
            }
            return a;
        }

        public static string NewOutDir(this string path)
        {
            var f0 = path;
            for (int i = 0; File.Exists(path) || Directory.Exists(path); i++) path = $"{f0}{i}";
            return path;
        }
        public static string NewOutFile(this string path)
        {
            var ext = Path.GetExtension(path);
            var f0 = Path.ChangeExtension(path, "");
            for (int i = 0; File.Exists(path) || Directory.Exists(path); i++) path = $"{f0}{i}{ext}";
            return path;
        }

        /// <summary>
        /// Unzip any 7zip compatible archive
        /// </summary>
        /// <param name="file"></param>
        /*public*/
        public static bool Unzip(this INode file, string dir)
        {
            try
            {
                //Create opuput directory
                using var a = file.ToArchive();
                Directory.CreateDirectory(dir);
                a.WriteToDirectory(dir, new ExtractionOptions { ExtractFullPath = true, PreserveFileTime = true });
                return true;
            }
            catch (Exception)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                return false;
            }
        }

        /// <summary>Extract with the selected engine. The external option used to be ignored.</summary>
        public static bool Unzip(this INode file, string dir, bool call7zip)
        {
            if (!call7zip) return file.Unzip(dir);
            try
            {
                var sevenZip = Apps.SevenZip;
                if (string.IsNullOrWhiteSpace(sevenZip)) return false;
                Directory.CreateDirectory(dir);
                var source = file.GetFileOrTempPath();
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = sevenZip,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "x", "-y", $"-o{dir}", source }
                });
                process?.WaitForExit();
                if (process?.ExitCode == 0) return true;
                try { Directory.Delete(dir, true); } catch { }
                return false;
            }
            catch
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Resolve an archive entry below an extraction root, rejecting rooted paths and
        /// parent traversal. Used by manual nested-archive extraction.
        /// </summary>
        internal static string SafeExtractionPath(string root, string entry)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(entry)) return null;
            var relative = entry.Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative)) return null;
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
        }

        internal static bool IsSafeArchivePath(string entry)
            => SafeExtractionPath(Path.Combine(Path.GetTempPath(), "archive-root"), entry) != null;

        public static void Zip(this string zip, bool call7zip, params string[] files)
        {
            if (call7zip)
            {
                var sevenZip = Apps.SevenZip;
                if (string.IsNullOrWhiteSpace(sevenZip))
                    throw new FileNotFoundException("7-Zip command-line executable (7z.exe or 7zz.exe) was not found.");
                var archiveType = zip.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ? "7z" : "zip";
                var args = $"a -t{archiveType} \"{zip}\" {string.Join(" ", files.Select(x => $"\"{x}\""))}";
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = sevenZip,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }) ?? throw new InvalidOperationException("7-Zip could not be started.");
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"7-Zip exited with code {process.ExitCode}.");
                return;
            }

            var entries = files.Where(x => File.Exists(x)).Select(x => (file: x, path: Path.GetFileName(x))).Union(
                files.Where(x => Directory.Exists(x)).SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)
                .Select(f => (file: f, path: f.Substring((files.Length == 1 ? d : Path.GetDirectoryName(d)).Length + 1))))).ToArray();

            if (zip.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            {
                using var a = ArchiveFactory.CreateArchive<SevenZipWriterOptions>();
                foreach (var f in entries) a.AddEntry(f.path, f.file);
                a.SaveTo(zip, new SevenZipWriterOptions { CompressionType = CompressionType.LZMA });
            }
            else
            {
                using var a = ArchiveFactory.CreateArchive<ZipWriterOptions>();
                foreach (var f in entries) a.AddEntry(f.path, f.file);
                a.SaveTo(zip, new ZipWriterOptions(CompressionType.Deflate));
            }
        }

        /// <summary>
        /// Extracts csv sheets from xlsx like file
        /// </summary>
        /// <param name="file"></param>
        /// <returns></returns>
        public static IEnumerable<string> GetExcelSheets(this INode file)
        {
            try
            {
                using var a = file.ToArchive();

                //Styles
                string defaultFormat = null;
                string[] FormatCodes = null;
                string FormatCode(string style)
                {
                    if (FormatCodes == null)
                    {
                        var styles = a.Entries.FirstOrDefault(x => x.Key == "xl/styles.xml");
                        if (styles == null)
                        {
                            defaultFormat = "GENERAL";
                            FormatCodes = Array.Empty<string>();
                            return defaultFormat;
                        }
                        using var s = styles.Load();
                        var xml = XDocument.Load(s);
                        var ns = xml.Root.GetDefaultNamespace();
                        var numFmts = xml.Descendants(ns + "numFmt").ToDictionary(x => x.Attribute("numFmtId").Value, x => x.Attribute("formatCode").Value);
                        //??? - where are defined the build in values?
                        numFmts["14"] = "YYYY-MM-DD";
                        numFmts["22"] = "YYYY-MM-DD\\ HH:MM:SS";
                        string numFmt(string key) => numFmts.TryGetValue(key, out var v) ? v : "GENERAL";
                        defaultFormat = numFmt(xml.Root.Element(ns + "cellStyleXfs").Elements().First().Attribute("numFmtId").Value);
                        FormatCodes = xml.Root.Element(ns + "cellXfs").Elements().Select(x => numFmt(x.Attribute("numFmtId").Value)).ToArray();
                    }
                    return style == null || !int.TryParse(style, out var index) || index < 0 || index >= FormatCodes.Length
                        ? defaultFormat
                        : FormatCodes[index];
                }

                //Shared strings
                var sharedEntry = a.Entries.FirstOrDefault(x => x.Key == "xl/sharedStrings.xml");
                var SharedStrings = Array.Empty<string>();
                XDocument xml = null;
                XNamespace ns = null;
                if (sharedEntry != null)
                {
                    using var ss = sharedEntry.Load();
                    xml = XDocument.Load(ss);
                    ns = xml.Root.GetDefaultNamespace();
                    SharedStrings = xml.Descendants(ns + "si")
                        .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))).ToArray();
                }

                //Sheets
                string Value(XElement e) => e.Element(ns + "v")?.Value ?? "";
                foreach (var sheet in a.Entries.Where(x => x.Key.StartsWith("xl/worksheets/sheet")).OrderBy(x => x.Key))
                {
                    using var s = sheet.Load();
                    s.Position = 0;
                    xml = XDocument.Load(s);
                    ns = xml.Root.GetDefaultNamespace();
                    yield return
                        string.Join("\r\n",
                        xml.Descendants(ns + "row").Select(r => string.Join(", ", r.Descendants(ns + "c")
                        .Select(x => x.Attribute("t")?.Value switch
                        {
                            "s" => SharedStrings[long.Parse(Value(x))], //Shared string
                            "inlineStr" => x.Value,
                            _ => FormatCode(x.Attribute("s")?.Value) switch
                            {
                                "YYYY-MM-DD" => DateTime.FromOADate(double.Parse(Value(x), CultureInfo.InvariantCulture)).ToString("yyyy-MM-dd"),
                                "YYYY-MM-DD\\ HH:MM:SS" => DateTime.FromOADate(double.Parse(Value(x), CultureInfo.InvariantCulture)).ToString("yyyy-MM-dd HH:mm:ss"),
                                _ => Value(x) //Unknown leave as it is
                            }
                        }))));
                }
            }
            finally { }
        }
    }
}
