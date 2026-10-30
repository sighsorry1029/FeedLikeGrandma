using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FeedLikeGrandma.OrbDiagnostics;

internal static class OrbDataReaderChecks
{
    private const string PayloadKey = "flg.tame.payload";
    private const string OrbKey = "flg.tame.orbId";
    private const string CmKey = "flg.tame.creaturemanager";
    private const string PendingKey = "flg.tame.pending";
    private const string Tx = "00112233445566778899aabbccddeeff";
    private const string Orb = "ffeeddccbbaa99887766554433221100";
    private static int checks;

    private static void Main()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            PayloadChecks();
            PendingChecks();
            RecoveryChecks();
            BoundedInputChecks();
            checks += ReportChecks.Run();
            Console.WriteLine("PASS: " + checks + " read-only OrbDataReader/report checks (no Unity/game runtime).");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    private static void PayloadChecks()
    {
        var fresh = Read(new Dictionary<string, string>());
        Equal(fresh, "payload.status", "absent");
        Equal(fresh, "pending.status", "absent");
        Equal(fresh, "orbId.validGuidN", "false");
        var v3 = ReadPayload(Payload(3));
        Equal(v3, "payload.status", "valid");
        Equal(v3, "payload.version", "3");
        Equal(v3, "payload.prefab", "Wolf");
        Equal(v3, "payload.level", "3");
        Equal(v3, "payload.health", "42.5");
        Equal(v3, "payload.cuisine.status", "not_stored_in_v3");
        Equal(v3, "payload.inventory.status", "base64_only_not_inventory_validated");
        var v4 = ReadPayload(Payload(4, "1|36000000000|4|1"));
        Equal(v4, "payload.status", "valid");
        Equal(v4, "payload.cuisine.birthCount", "4");
        Equal(v4, "payload.cuisine.remainingTicks", "36000000000");
        Equal(v4, "payload.cuisine.hasBeenPregnant", "true");
        Equal(ReadPayload(Payload(4)), "payload.cuisine.status", "empty_no_saved_state");
        Equal(ReadPayload(Payload(4, "1|72000000000|0|0")), "payload.status", "valid");
        foreach (string bad in new[] { "1|72000000001|0|0", "1|-1|0|0", "1|+1|0|0", "1|1|-1|0", "1|1|0|2", "2|1|0|0", "1|1|0|0|extra" })
            Equal(ReadPayload(Payload(4, bad)), "payload.status", "malformed");
        Equal(ReadPayload(Append(Payload(3), 123)), "payload.status", "valid");
        Equal(ReadPayload(Append(Payload(3), 123)), "payload.trailingBytes", "1");
        Equal(ReadPayload(Append(Payload(4), 123)), "payload.status", "malformed");
        Equal(ReadPayload(Encode(w => w.Write(5))), "payload.status", "unsupported");
        Equal(ReadPayload("not base64!"), "payload.status", "malformed");
        Equal(ReadPayload(""), "payload.status", "malformed");
        Equal(ReadPayload(Payload(4, "", "   ")), "payload.status", "malformed");
        var legacyOpaque = ReadPayload(Payload(3, "", "Wolf", "inventory-v1"));
        Equal(legacyOpaque, "payload.status", "valid");
        Equal(legacyOpaque, "payload.inventory.status", "malformed");
        Equal(ReadPayload(Payload(3, "", "Wolf", "", float.NaN)), "payload.status", "valid");
        Equal(ReadPayload(Payload(3, "", "Wolf", "", float.NaN)), "payload.health", "NaN");
        var largeDisplay = ReadPayload(Payload(4, "", new string('x', 10000)));
        Check(largeDisplay["payload.prefab"].Length < 300, "display fields are bounded");
    }

    private static void PendingChecks()
    {
        string payload = Payload(4, "1|1|2|1");
        var empty = new Dictionary<string, string> { [OrbKey] = Orb, [PendingKey] = Pending() };
        var facts = Read(empty);
        Equal(facts, "pending.status", "valid");
        Equal(facts, "pending.action", "Capture");
        Equal(facts, "pending.phase", "Preparing");
        Equal(facts, "pending.request.status", "empty");
        Equal(facts, "pending.currentPayloadMatchesPrevious", "true");
        Equal(facts, "pending.orbIdMatchesItem", "true");
        Equal(facts, "orbId.value", Orb);
        Equal(facts, "orbId.validGuidN", "true");

        var capture = new Dictionary<string, string>
        {
            [OrbKey] = Orb, [PayloadKey] = payload, [CmKey] = "cm-value",
            [PendingKey] = Pending(phase: 2, request: CaptureRequest(), prepared: Prepared(payload))
        };
        facts = Read(capture);
        Equal(facts, "pending.status", "valid");
        Equal(facts, "pending.currentPayloadMatchesPrevious", "false");
        Equal(facts, "pending.request.transactionIdMatchesPending", "true");
        Equal(facts, "pending.request.orbIdMatchesPending", "true");
        Equal(facts, "pending.prepared.status", "parsed");
        Equal(facts, "pending.prepared.currentPayloadEqualsPrepared", "true");
        Equal(facts, "pending.prepared.currentCreatureManagerValueEqualsPrepared", "true");
        Equal(facts, "pending.prepared.payload.cuisine.birthCount", "2");

        var release = new Dictionary<string, string>
        {
            [OrbKey] = Orb, [PayloadKey] = payload, [CmKey] = "cm-value",
            [PendingKey] = Pending(action: 2, phase: 3, spawned: "123:456", request: ReleaseRequest(payload),
                hadPayload: true, previousPayload: payload, hadCm: true, previousCm: "cm-value")
        };
        facts = Read(release);
        Equal(facts, "pending.status", "valid");
        Equal(facts, "pending.spawnedId", "123:456");
        Equal(facts, "pending.currentPayloadMatchesPrevious", "true");
        Equal(facts, "pending.request.status", "parsed");
        Equal(facts, "pending.request.payloadMatchesPrevious", "true");
        Equal(facts, "pending.request.creatureManagerMatchesPrevious", "true");
        Equal(facts, "pending.request.spawnPosition", "1,2,3");
        Equal(facts, "pending.request.spawnRotation", "0,0,0,1");
        release[OrbKey] = Tx;
        Equal(Read(release), "pending.orbIdMatchesItem", "false");

        foreach (string bad in new[]
        {
            Pending(tx: "bad"), Pending(orb: "bad"), Pending(world: 0), Pending(stack: 0), Pending(variant: -1),
            Pending(action: 1, phase: 3), Pending(action: 2, phase: 2), Pending(action: 3), Pending(phase: 0),
            Pending(spawned: "1:2"), Pending(action: 2, phase: 3, spawned: "0:0"),
            Pending(action: 2, phase: 3, spawned: "1:-1"), Pending(action: 2, phase: 3, spawned: "1:2:3"),
            Pending(action: 2, phase: 1, spawned: "1:2"), Pending(action: 2, prepared: "anything"),
            Pending(previousPayload: payload), Pending(previousCm: "data"), Append(Pending(), 1)
        }) Equal(ReadPending(bad), "pending.status", "malformed");
        Equal(ReadPending(Pending(action: 2, phase: 3, spawned: "-1:0")), "pending.status", "valid");
        Equal(ReadPending(Pending(action: 2, phase: 3, spawned: "-9223372036854775808:4294967295")), "pending.status", "valid");
        Equal(ReadPending(Pending(action: 2, phase: 3, spawned: " 1 :4294967295")), "pending.status", "valid");
        Equal(ReadPending(Pending(action: 2, phase: 3, spawned: "1:+1")), "pending.status", "malformed");
        Equal(ReadPending(Pending(action: 2, phase: 3, spawned: "1:4294967296")), "pending.status", "malformed");
        Equal(ReadPending(Pending(action: 2, phase: 3, spawned: "1:" + new string('0', 29) + "1")), "pending.status", "valid");
        Equal(ReadPending(Pending(action: 2, phase: 3, spawned: "1:" + new string('0', 30) + "1")), "pending.status", "malformed");
        Equal(ReadPending(Pending(action: 2, phase: 3)), "pending.status", "valid");
        Equal(ReadPending(Pending(phase: 2)), "pending.status", "valid");
        Equal(ReadPending(Encode(w => w.Write(2))), "pending.status", "unsupported");
        Equal(ReadPending(Pending(request: "invalid!")), "pending.status", "valid");
        Equal(ReadPending(Pending(request: "invalid!")), "pending.request.status", "malformed");
        Equal(ReadPending(Pending(prepared: "invalid!")), "pending.prepared.status", "malformed");
        Equal(ReadPending(Pending(hadPayload: true)), "pending.status", "valid");
        Equal(ReadPending(Pending(hadPayload: true)), "pending.previous.payload.status", "malformed");
        Equal(ReadPending(Pending(request: CaptureRequest(Tx, Tx))), "pending.request.orbIdMatchesPending", "false");
        Equal(ReadPending(Pending(prepared: Prepared(payload, Orb))), "pending.prepared.transactionIdMatchesEnvelope", "false");
        Equal(ReadPending(Pending(hadPayload: true, previousPayload: new string('a', 1024 * 1024 + 1))), "pending.status", "malformed");
    }

    private static void RecoveryChecks()
    {
        byte[] packet = Bytes(w => { w.Write(777L); w.Write(Tx); w.Write(Orb); w.Write("Committing"); w.Write(Prepared(Payload(4))); });
        byte[] before = (byte[])packet.Clone();
        var facts = OrbDataReader.ReadRecoveryReply(packet);
        Equal(facts, "recovery.status", "parsed");
        Equal(facts, "recovery.worldUid", "777");
        Equal(facts, "recovery.transactionId", Tx);
        Equal(facts, "recovery.orbId", Orb);
        Equal(facts, "recovery.state", "Committing");
        Equal(facts, "recovery.knownState", "true");
        Equal(facts, "recovery.prepared.payload.status", "valid");
        Check(packet.SequenceEqual(before), "recovery bytes unchanged");
        facts = OrbDataReader.ReadRecoveryReply(Bytes(w => { w.Write(777L); w.Write(Tx); w.Write(Orb); w.Write("future-state"); w.Write(""); w.Write((byte)4); }));
        Equal(facts, "recovery.status", "parsed");
        Equal(facts, "recovery.knownState", "false");
        Equal(facts, "recovery.trailingBytes", "1");
        Equal(OrbDataReader.ReadRecoveryReply(new byte[1]), "recovery.status", "malformed");
    }

    private static void BoundedInputChecks()
    {
        string enormousLength = Encode(w => { w.Write(4); w.Write(new byte[] { 0xff, 0xff, 0xff, 0xff, 0x07 }); });
        Equal(ReadPayload(enormousLength), "payload.status", "malformed");
        string invalidLength = Encode(w => { w.Write(4); w.Write(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0x00 }); });
        Equal(ReadPayload(invalidLength), "payload.status", "malformed");
        Equal(ReadPayload(Convert.ToBase64String(new byte[OrbDataReader.MaxDecodedBytes + 1])), "payload.status", "uninspected_oversize");
        Equal(ReadPending(Convert.ToBase64String(new byte[OrbDataReader.MaxDecodedBytes + 1])), "pending.status", "uninspected_oversize");
        Equal(OrbDataReader.ReadRecoveryReply(new byte[OrbDataReader.MaxDecodedBytes + 1]), "recovery.status", "uninspected_oversize");
        Equal(ReadPayload(new string(' ', 6 * 1024 * 1024)), "payload.status", "uninspected_oversize");
        string longUnicode = new string('a', 2047) + "\ud83d\ude00" + new string('b', 4097);
        var original = new Dictionary<string, string> { [CmKey] = longUnicode, [OrbKey] = Orb, [PendingKey] = "invalid!" };
        var before = original.ToArray();
        var facts = Read(original);
        using (var sha = SHA256.Create())
            Equal(facts, "keys." + CmKey + ".sha256Utf8", BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(longUnicode))).Replace("-", "").ToLowerInvariant());
        Check(original.OrderBy(p => p.Key).SequenceEqual(before.OrderBy(p => p.Key)), "input customData unchanged");
        Check(facts.OrderBy(p => p.Key).SequenceEqual(Read(original).OrderBy(p => p.Key)), "repeat inspection deterministic");
        Equal(ReadPayload(Payload(4, "", "Wolf\r\nInjected")), "payload.prefab", "Wolf\\u000d\\u000aInjected");
        foreach (string value in new[]
        {
            "", "abc", "길들인 록스와 늑대의 상태", new string('a', 2048), new string('한', 4097),
            new string('x', 2047) + "\ud83d\ude00" + new string('y', 1024 * 1024),
            new string('x', 2047) + "\ud83d" + new string('y', 2049), "\ude00", "\ud83d"
        })
        {
            using (var sha = SHA256.Create())
                Equal(Read(new Dictionary<string, string> { [CmKey] = value }), "keys." + CmKey + ".sha256Utf8",
                    BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant());
        }
    }

    private static Dictionary<string, string> Read(Dictionary<string, string> data) { return OrbDataReader.Read(data); }
    private static Dictionary<string, string> ReadPayload(string payload) { return Read(new Dictionary<string, string> { [PayloadKey] = payload }); }
    private static Dictionary<string, string> ReadPending(string pending) { return Read(new Dictionary<string, string> { [PendingKey] = pending }); }
    private static string Payload(int version, string cuisine = "", string prefab = "Wolf", string inventory = "AQID", float health = 42.5f)
    {
        return Encode(w =>
        {
            w.Write(version); w.Write(prefab); w.Write("Grandma"); w.Write(3); w.Write(health); w.Write(100f);
            w.Write(true); w.Write(inventory); w.Write(123456789L); w.Write(987654321L); w.Write(12.5f); w.Write(45.25f);
            w.Write(2); w.Write(76.5f); w.Write("Fire"); w.Write("Armored"); w.Write(true);
            if (version == 4) w.Write(cuisine);
        });
    }
    private static string Pending(string tx = Tx, string orb = Orb, long world = 777, byte action = 1, byte phase = 1,
        string spawned = "", string request = "", string prepared = "", bool hadPayload = false, string previousPayload = "",
        bool hadCm = false, string previousCm = "", int variant = 0, int stack = 1)
    {
        return Encode(w =>
        {
            w.Write(1); w.Write(tx); w.Write(orb); w.Write(world); w.Write(action); w.Write(phase); w.Write(spawned);
            w.Write(request); w.Write(prepared); w.Write(hadPayload); w.Write(previousPayload); w.Write(hadCm); w.Write(previousCm);
            w.Write(variant); w.Write(stack);
        });
    }
    private static string CaptureRequest(string tx = Tx, string orb = Orb)
    {
        return Encode(w => { w.Write(tx); ZdoId(w, 11, 22); ZdoId(w, 33, 44); w.Write(orb); w.Write("Wolf"); });
    }
    private static string ReleaseRequest(string payload)
    {
        return Encode(w => { w.Write(Tx); ZdoId(w, 11, 22); w.Write(Orb); w.Write(payload); w.Write("cm-value");
            foreach (float number in new[] { 1f, 2f, 3f, 0f, 0f, 0f, 1f }) w.Write(number); });
    }
    private static string Prepared(string payload, string tx = Tx)
    {
        return Encode(w => { w.Write(tx); w.Write(true); w.Write(false); ZdoId(w, 33, 44); w.Write("Wolf"); w.Write(payload); w.Write("cm-value"); w.Write(""); });
    }
    private static void ZdoId(BinaryWriter writer, long user, uint id) { writer.Write(user); writer.Write(id); }
    private static string Append(string encoded, byte last) { return Convert.ToBase64String(Convert.FromBase64String(encoded).Concat(new[] { last }).ToArray()); }
    private static string Encode(Action<BinaryWriter> write) { return Convert.ToBase64String(Bytes(write)); }
    private static byte[] Bytes(Action<BinaryWriter> write)
    {
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) write(writer);
            return stream.ToArray();
        }
    }
    private static void Equal(Dictionary<string, string> facts, string key, string expected)
    {
        string actual;
        Check(facts.TryGetValue(key, out actual) && actual == expected, key + ": expected " + expected + ", got " + (actual ?? "<missing>"));
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }
}
