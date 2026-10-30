using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using FeedLikeGrandma.OrbDiagnostics;

internal static class ReportChecks
{
    private static int checks;

    internal static int Run()
    {
        checks = 0;
        RoundTripAndSave();
        StorageLimitsAndFailureChecks();
        ComparisonChecks();
        TextLimitChecks();
        return checks;
    }

    private static DiagnosticReport Report()
    {
        return new DiagnosticReport
        {
            Kind = "test", StartedUtc = "2026-10-21T00:00:00.0000000Z", FinishedUtc = "2026-10-21T00:01:00.0000000Z", StopReason = "test completed"
        };
    }

    private static void RoundTripAndSave()
    {
        string raw = "한글 록스\r\n\t\"\\\u0001 이모지 \ud83d\ude00";
        string large = "RAW_ONLY_START_" + new string('한', 5000) + "_RAW_ONLY_END";
        var report = Report();
        report.Environment["serverContext"] = "client\r\nobserved";
        report.Coverage.Add("handler\ninstalled");
        report.DroppedEvents = 7;
        var item = new ItemSnapshot { Label = "I1" };
        item.RawFlgData["flg.tame.payload"] = raw;
        item.RawFlgData["flg.tame.creaturemanager"] = large;
        item.RawFlgData["flg.tame.orbId"] = null;
        item.Facts["summary"] = "line1\r\nline2\tend";
        item.Facts["longSummary"] = new string('x', 3000);
        var snapshot = new InventorySnapshot { Label = "before", Utc = "2026-10-21T00:00:10.0000000Z" };
        snapshot.Items.Add(item);
        report.Snapshots.Add(snapshot);
        report.Events.Add(new TraceEntry { Call = 2, Name = "RecoveryReply", Phase = "entry", Utc = "2026-10-21T00:00:20.0000000Z",
            Facts = new Dictionary<string, string> { ["state"] = "unknown", ["text"] = raw } });

        var serializer = Serializer();
        DiagnosticReport copy;
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, report);
            stream.Position = 0;
            copy = (DiagnosticReport)serializer.ReadObject(stream);
        }
        Check(copy.Schema == "flg-orb-diagnostics/1", "schema survives JSON roundtrip");
        Check(copy.Snapshots[0].Items[0].RawFlgData["flg.tame.payload"] == raw, "JSON preserves raw Unicode, controls, quotes and slashes");
        Check(copy.Snapshots[0].Items[0].RawFlgData["flg.tame.creaturemanager"] == large, "JSON preserves long raw data without text truncation");
        Check(copy.Snapshots[0].Items[0].RawFlgData["flg.tame.orbId"] == null, "JSON preserves null raw value");
        Check(copy.Events[0].Facts["text"] == raw, "trace fact raw value survives JSON roundtrip");
        Check(copy.DroppedEvents == 7, "dropped event count survives JSON roundtrip");
        string readable = report.RenderText();
        Check(readable.Contains("summary = line1\\r\\nline2\\tend"), "readable facts escape line and tab controls");
        Check(readable.Contains("client\\r\\nobserved"), "environment facts escape line breaks");
        Check(readable.Contains("handler\\ninstalled"), "coverage entries escape line breaks");
        Check(readable.Contains("[text truncated; see JSON]"), "readable long facts include truncation marker");
        Check(!readable.Contains(large), "readable text does not leak full raw-only data");
        Check(!readable.Contains(new string('x', 3000)), "readable fact is truncated");
        Check(report.RenderText() == readable, "render text does not mutate report");
        Check(copy.RenderText() == readable, "roundtrip report renders identical text");

        string directory = Path.Combine(Path.GetTempPath(), "flg-orb-report-check-" + Guid.NewGuid().ToString("N"));
        string stem = null;
        try
        {
            stem = report.Save(directory);
            Check(File.Exists(stem + ".json") && File.Exists(stem + ".txt"), "Save creates both report files");
            Check(File.ReadAllText(stem + ".txt", Encoding.UTF8) == readable, "Save text equals RenderText");
            using (var stream = File.OpenRead(stem + ".json")) copy = (DiagnosticReport)Serializer().ReadObject(stream);
            Check(copy.Snapshots[0].Items[0].RawFlgData["flg.tame.payload"] == raw, "saved JSON retains exact raw data");
            Check(copy.Snapshots[0].Items[0].RawFlgData["flg.tame.creaturemanager"] == large, "saved JSON retains full long raw data");
        }
        finally
        {
            // Only these test-created files are removed; do not delete recursively.
            if (stem != null)
            {
                if (File.Exists(stem + ".json")) File.Delete(stem + ".json");
                if (File.Exists(stem + ".txt")) File.Delete(stem + ".txt");
            }
            if (File.Exists(Path.Combine(directory, ".reports.lock"))) File.Delete(Path.Combine(directory, ".reports.lock"));
            if (Directory.Exists(directory)) Directory.Delete(directory, false);
        }
    }

    private static void StorageLimitsAndFailureChecks()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "flg-orb-report-limits-" + Guid.NewGuid().ToString("N")));
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture must remain inside temporary directory.");
        Directory.CreateDirectory(root);
        try
        {
            string countDir = Path.Combine(root, "count");
            for (int i = 0; i < DiagnosticReport.MaximumSavedReports; i++) Report().Save(countDir);
            Check(Directory.GetFiles(countDir, "*.json").Length == DiagnosticReport.MaximumSavedReports &&
                Directory.GetFiles(countDir, "*.txt").Length == DiagnosticReport.MaximumSavedReports,
                "report quota counts paired stems once and permits exactly the limit");
            Dictionary<string, byte[]> original = FileBytes(countDir);
            ExpectIo(() => Report().Save(countDir), "one report above the count cap is rejected");
            CheckFilesUnchanged(countDir, original, "count rejection preserves every existing report and leaves no partial files");

            string orphanDir = Path.Combine(root, "orphans");
            Directory.CreateDirectory(orphanDir);
            for (int i = 0; i < DiagnosticReport.MaximumSavedReports; i++)
                File.WriteAllText(Path.Combine(orphanDir, "orphan-" + i + (i % 2 == 0 ? ".json" : ".txt")), "preserved orphan " + i);
            original = FileBytes(orphanDir);
            ExpectIo(() => Report().Save(orphanDir), "orphan JSON and text files count toward report quota");
            CheckFilesUnchanged(orphanDir, original, "orphan quota rejection never deletes or repairs existing evidence");

            string byteDir = Path.Combine(root, "bytes");
            Directory.CreateDirectory(byteDir);
            string filler = Path.Combine(byteDir, "previous-incomplete.json.tmp");
            using (var file = new FileStream(filler, FileMode.CreateNew, FileAccess.Write))
            {
                file.WriteByte(73);
                file.SetLength(DiagnosticReport.MaximumSavedBytes - 64);
            }
            var pending = Report();
            ExpectIo(() => pending.Save(byteDir), "stale temporary bytes count and in-progress writes cannot exceed total quota");
            Check(new FileInfo(filler).Length == DiagnosticReport.MaximumSavedBytes - 64 && File.ReadAllBytes(Path.Combine(byteDir, ".reports.lock")).Length == 0,
                "quota failure preserves stale evidence and leaves only an empty coordination file");
            using (var file = File.OpenRead(filler)) Check(file.ReadByte() == 73, "quota failure preserves existing bytes");
            Check(Directory.GetFiles(byteDir).All(path => path == filler || Path.GetFileName(path) == ".reports.lock"),
                "write-budget failure removes only this invocation's temporary files");
            File.Delete(filler);
            string saved = pending.Save(byteDir);
            Check(File.Exists(saved + ".json") && File.Exists(saved + ".txt"), "same pending report can save after capacity is freed");

            string unrelatedDir = Path.Combine(root, "unrelated");
            Directory.CreateDirectory(unrelatedDir);
            string unrelated = Path.Combine(unrelatedDir, "operator-evidence.bin");
            using (var file = new FileStream(unrelated, FileMode.CreateNew, FileAccess.Write)) file.SetLength(DiagnosticReport.MaximumSavedBytes);
            ExpectIo(() => Report().Save(unrelatedDir), "non-report files also consume the byte quota");
            Check(new FileInfo(unrelated).Length == DiagnosticReport.MaximumSavedBytes &&
                Directory.GetFiles(unrelatedDir).All(path => path == unrelated || Path.GetFileName(path) == ".reports.lock"),
                "full directory rejection preserves unrelated files");

            string leaseDir = Path.Combine(root, "lease");
            Directory.CreateDirectory(leaseDir);
            using (var lease = new FileStream(Path.Combine(leaseDir, ".reports.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                ExpectIo(() => Report().Save(leaseDir), "held report lease rejects a concurrent writer");
                Check(Directory.GetFiles(leaseDir).Length == 1, "busy writer creates no report files");
            }
            string afterLease = Report().Save(leaseDir);
            Check(File.Exists(afterLease + ".json") && File.Exists(afterLease + ".txt"), "disposed lease allows later save");

            string failedDir = Path.Combine(root, "render-failure");
            Directory.CreateDirectory(failedDir);
            string retained = Path.Combine(failedDir, "operator.json");
            File.WriteAllText(retained, "old evidence");
            var broken = Report();
            broken.Coverage = null;
            bool failed = false;
            try { broken.Save(failedDir); } catch (Exception) { failed = true; }
            Check(failed, "rendering failure propagates for caller to retain the report");
            Check(File.ReadAllText(retained) == "old evidence" && Directory.GetFiles(failedDir).All(path => path == retained || Path.GetFileName(path) == ".reports.lock"),
                "rendering failure leaves no partial new pair and preserves preexisting report");

            string nestedDir = Path.Combine(root, "top-level");
            string nested = Path.Combine(nestedDir, "operator-backups");
            Directory.CreateDirectory(nested);
            string child = Path.Combine(nested, "archive.bin");
            using (var file = new FileStream(child, FileMode.CreateNew, FileAccess.Write)) file.SetLength(DiagnosticReport.MaximumSavedBytes);
            string outer = Report().Save(nestedDir);
            Check(File.Exists(outer + ".json") && new FileInfo(child).Length == DiagnosticReport.MaximumSavedBytes,
                "retention is top-level only and never enumerates or changes nested backups");

            var unsafeName = Report();
            unsafeName.Kind = "../outside";
            bool nameRejected = false;
            try { unsafeName.Save(nestedDir); } catch (Exception) { nameRejected = true; }
            Check(nameRejected && Directory.GetFiles(root, "*outside*", SearchOption.AllDirectories).Length == 0,
                "invalid report kind cannot escape the intended directory");
        }
        finally
        {
            // This invocation owns the absolute temporary directory checked above.
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static Dictionary<string, byte[]> FileBytes(string directory) => Directory.GetFiles(directory)
        .Where(path => Path.GetFileName(path) != ".reports.lock")
        .ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);

    private static void CheckFilesUnchanged(string directory, Dictionary<string, byte[]> before, string message)
    {
        Dictionary<string, byte[]> after = FileBytes(directory);
        Check(before.Count == after.Count && before.All(pair => after.ContainsKey(pair.Key) && pair.Value.SequenceEqual(after[pair.Key])), message);
    }

    private static void ExpectIo(Action action, string message)
    {
        try { action(); }
        catch (IOException) { Check(true, message); return; }
        // Original Unity Mono's interpreted DataContractJsonSerializer wraps a
        // stream's IOException in reflection invocation wrappers. The production
        // caller catches Exception and retains evidence in both runtimes.
        catch (TargetInvocationException error)
        {
            Exception cause = error;
            while (cause is TargetInvocationException && cause.InnerException != null) cause = cause.InnerException;
            if (!(cause is IOException)) throw;
            Check(true, message); return;
        }
        throw new InvalidOperationException(message + ": expected IOException");
    }

    private static void ComparisonChecks()
    {
        var report = Report();
        var snapshot = new InventorySnapshot { Label = "compare" };
        var first = new ItemSnapshot { Label = "old" };
        var second = new ItemSnapshot { Label = "new" };
        first.Facts["orbId.value"] = second.Facts["orbId.value"] = "same-id";
        first.Facts["duplicateOrbIdInThisInventory"] = second.Facts["duplicateOrbIdInThisInventory"] = "YES: old; new";
        first.Facts["runtimeItemRef"] = "old-ref";
        second.Facts["runtimeItemRef"] = "new-ref";
        first.Facts["slot"] = "1,1";
        second.Facts["slot"] = "2,1";
        first.Facts["pending.status"] = "malformed";
        second.Facts["pending.status"] = "absent";
        first.Facts["a.leftOnly"] = "left\nvalue";
        second.Facts["z.rightOnly"] = "right\tvalue";
        snapshot.Items.Add(first);
        snapshot.Items.Add(second);
        report.Snapshots.Add(snapshot);
        string text = report.RenderText();
        string comparison = ComparisonSection(text);
        Check(comparison.Contains("old <> new"), "comparison identifies the two snapshots");
        Check(comparison.Contains("pending.status: malformed <> absent"), "comparison reports differing states");
        Check(comparison.Contains("a.leftOnly: left\\nvalue <> <absent>"), "comparison reports left-only key and escapes controls");
        Check(comparison.Contains("z.rightOnly: <absent> <> right\\tvalue"), "comparison reports right-only key and escapes controls");
        Check(!comparison.Contains("runtimeItemRef") && !comparison.Contains("slot:"), "comparison ignores expected runtime ref and slot differences");
        Check(!comparison.Contains("orbId.value"), "same Orb ID alone is not reported as a difference");
        Check(!comparison.Contains("duplicateOrbIdInThisInventory"), "identical duplicate annotation is not a comparison difference");
        Check(text.Split(new[] { "duplicateOrbIdInThisInventory = YES: old; new" }, StringSplitOptions.None).Length - 1 == 2,
            "supplied duplicate annotation remains visible on both items");
        Check(comparison.IndexOf("a.leftOnly", StringComparison.Ordinal) < comparison.IndexOf("pending.status", StringComparison.Ordinal) &&
            comparison.IndexOf("pending.status", StringComparison.Ordinal) < comparison.IndexOf("z.rightOnly", StringComparison.Ordinal), "comparison keys have ordinal order");
    }

    private static void TextLimitChecks()
    {
        Check(DiagnosticReport.Safe(null) == "<null>", "Safe null value");
        Check(DiagnosticReport.Safe("\r\n\t") == "\\r\\n\\t", "Safe known line controls");
        Check(DiagnosticReport.Safe(new string('a', 2048)) == new string('a', 2048), "Safe keeps exact limit");
        Check(DiagnosticReport.Safe(new string('a', 2049)) == new string('a', 2048) + " [text truncated; see JSON]", "Safe truncates above limit");
        var report = Report();
        var snapshot = new InventorySnapshot { Label = "many" };
        for (int i = 1; i <= 9; i++)
            snapshot.Items.Add(new ItemSnapshot { Label = "I" + i, Facts = new Dictionary<string, string> { ["state"] = "value-" + i } });
        report.Snapshots.Add(snapshot);
        string text = report.RenderText();
        string comparison = ComparisonSection(text);
        Check(comparison.Contains("I7 <> I8"), "comparison includes eighth item");
        Check(!comparison.Contains("I9") && !comparison.Contains("value-9"), "comparison stops after eighth item");
        Check(text.Contains("ITEM I9") && text.Contains("state = value-9"), "ninth item facts remain visible");
        Check(comparison.Split(new[] { " <> " }, StringSplitOptions.None).Length - 1 == 56, "eight-item comparison has 28 pairs and 28 differing facts");
    }

    private static string ComparisonSection(string text)
    {
        int start = text.IndexOf("  Comparison (", StringComparison.Ordinal);
        int end = text.IndexOf("\nTRACE (", start, StringComparison.Ordinal);
        return text.Substring(start, end - start);
    }

    private static DataContractJsonSerializer Serializer()
    {
        return new DataContractJsonSerializer(typeof(DiagnosticReport), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        ++checks;
    }
}
