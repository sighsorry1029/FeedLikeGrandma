#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace FeedLikeGrandma.OrbDiagnostics
{
    [DataContract]
    internal sealed class DiagnosticReport
    {
        internal const int MaximumSavedReports = 20;
        internal const long MaximumSavedBytes = 128L * 1024 * 1024;
        [DataMember] public string Schema = "flg-orb-diagnostics/1";
        [DataMember] public string Kind;
        [DataMember] public string StartedUtc = DateTime.UtcNow.ToString("O");
        [DataMember] public string FinishedUtc;
        [DataMember] public string StopReason;
        [DataMember] public Dictionary<string, string> Environment = new Dictionary<string, string>();
        [DataMember] public List<string> Coverage = new List<string>();
        [DataMember] public List<InventorySnapshot> Snapshots = new List<InventorySnapshot>();
        [DataMember] public List<TraceEntry> Events = new List<TraceEntry>();
        [DataMember] public int DroppedEvents;
        [DataMember] public string Limits = "Client observations only. No server journal read, recovery request, item write, save, spawn or destroy. An observed RPC/handler entry is not proof of delivery, authorization or committed server outcome. Missing events can mean an unhooked/vetoed path. Duplicate IDs are checked only in this player's current inventory. Different Orb IDs/contents alone are normal. Raw FLG values may include creature names and world/transaction IDs; review before public sharing. Unsupported/oversize data is uninspected, not corrupt.";

        public string Save(string directory)
        {
            if (string.IsNullOrEmpty(Kind) || Kind.Length > 32 || Kind.Any(c =>
                !(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_' && c != '-'))
                throw new InvalidDataException("Invalid Orb diagnostic report kind.");
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            // Serialize quota checks/publication between processes using the same
            // profile. Keep this empty lease file; never remove another writer's lock.
            using (new FileStream(Path.Combine(directory, ".reports.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var reports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long usedBytes = 0;
                foreach (string path in Directory.EnumerateFiles(directory))
                {
                    if (Path.GetFileName(path) == ".reports.lock") continue;
                    usedBytes = checked(usedBytes + new FileInfo(path).Length);
                    string extension = Path.GetExtension(path);
                    if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                        reports.Add(Path.GetFileNameWithoutExtension(path));
                }
                if (reports.Count >= MaximumSavedReports || usedBytes >= MaximumSavedBytes)
                    throw StorageLimit();

                string stem = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                    + "-" + Kind + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string json = stem + ".json", text = stem + ".txt";
                string jsonTemporary = json + ".tmp", textTemporary = text + ".tmp";
                bool ownJsonTemporary = false, ownTextTemporary = false, ownJson = false, ownText = false, complete = false;
                try
                {
                    long remaining = MaximumSavedBytes - usedBytes;
                    var serializer = new DataContractJsonSerializer(typeof(DiagnosticReport),
                        new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
                    using (var stream = new BoundedReportStream(jsonTemporary, remaining))
                    {
                        ownJsonTemporary = true;
                        serializer.WriteObject(stream, this);
                        stream.Flush(true);
                        remaining -= stream.Length;
                    }
                    using (var stream = new BoundedReportStream(textTemporary, remaining))
                    {
                        ownTextTemporary = true;
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true))
                            writer.Write(RenderText());
                        stream.Flush(true);
                    }
                    // Both formats are complete before either final name is published.
                    // A failed second move removes only our own first file for retry.
                    File.Move(jsonTemporary, json);
                    ownJsonTemporary = false; ownJson = true;
                    File.Move(textTemporary, text);
                    ownTextTemporary = false; ownText = true;
                    complete = true;
                    return stem;
                }
                finally
                {
                    if (!complete)
                    {
                        if (ownJsonTemporary) DeleteOwnIncomplete(jsonTemporary);
                        if (ownTextTemporary) DeleteOwnIncomplete(textTemporary);
                        if (ownJson) DeleteOwnIncomplete(json);
                        if (ownText) DeleteOwnIncomplete(text);
                    }
                }
            }
        }

        private static IOException StorageLimit() => new IOException(
            "Orb diagnostic storage limit reached (20 reports / 128 MiB). Move old reports elsewhere, then switch OrbDiagnostics Off and On to retry. Existing reports were retained.");

        private static void DeleteOwnIncomplete(string path)
        {
            try { File.Delete(path); }
            catch (Exception) { /* Never delete an existing report to make room. Leftovers still count against the byte quota. */ }
        }

        // Enforce remaining disk space while serializing, without buffering a
        // second full copy of potentially large raw Orb data in managed memory.
        private sealed class BoundedReportStream : FileStream
        {
            private readonly long _maximum;
            internal BoundedReportStream(string path, long maximum)
                : base(path, FileMode.CreateNew, FileAccess.Write, FileShare.None) { _maximum = maximum; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count > _maximum - Position) throw StorageLimit();
                base.Write(buffer, offset, count);
            }
            public override void WriteByte(byte value)
            {
                if (Position >= _maximum) throw StorageLimit();
                base.WriteByte(value);
            }
        }

        internal string RenderText()
        {
            var text = new StringBuilder();
            text.AppendLine("FeedLikeGrandma Orb diagnostics / 클라이언트 관찰 보고서");
            text.AppendLine(StartedUtc + " -> " + FinishedUtc + " / " + Kind + " / " + StopReason);
            text.AppendLine("서버의 실제 저널/권한/최종 처리 여부는 이 보고서만으로 확정할 수 없습니다.");
            text.AppendLine("OrbID·내용 차이는 정상일 수 있습니다. pending, 동일 ID 중복, 월드 불일치와 사용 흐름을 함께 확인하세요.");
            text.AppendLine(Limits);
            AppendFacts(text, Environment, "  ");
            text.AppendLine("\nObservation coverage:");
            foreach (string entry in Coverage) text.AppendLine("  " + Safe(entry));
            foreach (InventorySnapshot snapshot in Snapshots)
            {
                text.AppendLine("\nSNAPSHOT " + snapshot.Label + " " + snapshot.Utc);
                AppendFacts(text, snapshot.Facts, "  ");
                foreach (ItemSnapshot item in snapshot.Items)
                {
                    text.AppendLine("\n  ITEM " + item.Label);
                    AppendFacts(text, item.Facts, "    ");
                }
                text.AppendLine("\n  Comparison (first 8 items; full item facts/raw values are in JSON):");
                for (int a = 0; a < Math.Min(8, snapshot.Items.Count); ++a)
                    for (int b = a + 1; b < Math.Min(8, snapshot.Items.Count); ++b)
                        AppendComparison(text, snapshot.Items[a], snapshot.Items[b]);
            }
            text.AppendLine("\nTRACE (entry/return are observations, not success guarantees):");
            foreach (TraceEntry entry in Events)
            {
                text.AppendLine(entry.Utc + " #" + entry.Call + " " + entry.Name + " " + entry.Phase);
                AppendFacts(text, entry.Facts, "  ");
            }
            text.AppendLine("Dropped events: " + DroppedEvents);
            text.AppendLine("\nRaw FLG item values are preserved in JSON subject to the reported export limits. No other mods' custom data is exported.");
            return text.ToString();
        }

        private static void AppendComparison(StringBuilder text, ItemSnapshot a, ItemSnapshot b)
        {
            text.AppendLine("    " + a.Label + " <> " + b.Label);
            foreach (string key in a.Facts.Keys.Union(b.Facts.Keys).OrderBy(key => key, StringComparer.Ordinal))
            {
                if (key == "runtimeItemRef" || key == "slot") continue;
                string av = a.Facts.TryGetValue(key, out string left) ? left : "<absent>";
                string bv = b.Facts.TryGetValue(key, out string right) ? right : "<absent>";
                if (av != bv) text.AppendLine("      " + Safe(key) + ": " + Safe(av) + " <> " + Safe(bv));
            }
        }

        private static void AppendFacts(StringBuilder text, Dictionary<string, string> facts, string prefix)
        {
            foreach (var fact in facts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                text.AppendLine(prefix + Safe(fact.Key) + " = " + Safe(fact.Value));
        }

        internal static string Safe(string value)
        {
            if (value == null) return "<null>";
            string result = value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return result.Length <= 2048 ? result : result.Substring(0, 2048) + " [text truncated; see JSON]";
        }

    }

    [DataContract]
    internal sealed class InventorySnapshot
    {
        [DataMember] public string Label;
        [DataMember] public string Utc = DateTime.UtcNow.ToString("O");
        [DataMember] public Dictionary<string, string> Facts = new Dictionary<string, string>();
        [DataMember] public List<ItemSnapshot> Items = new List<ItemSnapshot>();
    }

    [DataContract]
    internal sealed class ItemSnapshot
    {
        [DataMember] public string Label;
        [DataMember] public Dictionary<string, string> Facts = new Dictionary<string, string>();
        [DataMember] public Dictionary<string, string> RawFlgData = new Dictionary<string, string>();
    }

    [DataContract]
    internal sealed class TraceEntry
    {
        [DataMember] public string Utc = DateTime.UtcNow.ToString("O");
        [DataMember] public int Call;
        [DataMember] public string Name;
        [DataMember] public string Phase;
        [DataMember] public Dictionary<string, string> Facts = new Dictionary<string, string>();
    }
}
