using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;
using SharpCompress.Writers.Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            catch (Exception ex)
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
            if (a != null && a.Entries.Skip(1).FirstOrDefault() == null && a.Entries.First() is IArchiveEntry fe && fe.Key == null && fe.ToArchive() is IArchive sa)
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
            catch (Exception e)
            {
                return false;
            }
        }

        public static void Zip(this string zip, bool call7zip, params string[] files)
        {
            if (call7zip)
            {
                var args = $"a -tzip \"{zip}\" {string.Join(" ", files.Select(x => $"\"{x}\""))}";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "7z.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }).WaitForExit();
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
                        using var s = a.Entries.First(x => x.Key == "xl/styles.xml").Load(); s.Position = 0;
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
                    return style == null ? defaultFormat : FormatCodes[long.Parse(style)];
                }

                //Shared strings
                using var ss = a.Entries.First(x => x.Key == "xl/sharedStrings.xml").Load(); ss.Position = 0;
                var xml = XDocument.Load(ss);
                var ns = xml.Root.GetDefaultNamespace();
                var SharedStrings = xml.Descendants(ns + "t").Select(x => x.Value).ToArray();

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
                                "YYYY-MM-DD" => DateTime.FromOADate(double.Parse(Value(x))).ToString("yyyy-MM-dd"),
                                "YYYY-MM-DD\\ HH:MM:SS" => DateTime.FromOADate(double.Parse(Value(x))).ToString("yyyy-MM-dd HH:mm:ss"),
                                _ => Value(x) //Unknown leave as it is
                            }
                        }))));
                }
            }
            finally { }
        }
    }
}
