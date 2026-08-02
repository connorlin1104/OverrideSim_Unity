using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEditor;

// Validates the inbox's two message shapes and the memory that keeps a note from coming back.
//
// The risk this guards against: the inbox is the ONLY channel back to a player who sent a robot in,
// and every failure in it is silent from the developer's side. A note that repeats forever, a note
// that never repeats even after being rewritten, an arrival mistaken for a note (so no code is
// added), a note mistaken for an arrival (so a nonexistent code is banked) — none of these throw,
// none show up in a log, and the only person who sees them is the player.
//
// The seen-key fingerprint is the sharpest edge. It must be stable ACROSS LAUNCHES, which rules out
// string.GetHashCode (randomized per process on .NET Core), and a test inside one process cannot
// observe that difference — so this pins the exact bytes instead, which is a thing that can only
// change deliberately.
//
// Runs on plain objects: no scene, no catalog, no asset. It does use the real PlayerPrefs key (that
// IS the storage), so it snapshots and restores it.
//
// Usage: Tools > RoboSim > Validation > Validate Robot Inbox.
// Batch: -executeMethod RobotInboxValidation.RunBatchValidate.
public static class RobotInboxValidation
{
    [MenuItem("Tools/RoboSim/Validation/Validate Robot Inbox", false, 5)]
    private static void RunFromMenu()
    {
        string result = Validate();
        EditorUtility.DisplayDialog("Validate Robot Inbox", result, "OK");
        Debug.Log(result);
    }

    public static void RunBatchValidate()
    {
        string result = Validate();
        Debug.Log(result);
        if (result.StartsWith("FAILED")) throw new System.InvalidOperationException(result);
    }

    private class Checks
    {
        public readonly List<string> Failures = new List<string>();
        public int Count;

        public void That(bool condition, string failureMessage)
        {
            Count++;
            if (!condition) Failures.Add(failureMessage);
        }
    }

    private static RobotInboxService.Item Arrival(string name, string code)
        => new RobotInboxService.Item { robotName = name, code = code, message = string.Empty };

    private static RobotInboxService.Item Note(string name, string message, string id = null)
        => new RobotInboxService.Item { id = id, robotName = name, message = message };

    private static string Validate()
    {
        string savedSeen = PlayerPrefs.GetString(RobotInboxSettings.SeenPrefKey, string.Empty);
        var checks = new Checks();

        try
        {
            CheckClassification(checks);
            CheckKeys(checks);
            CheckSeenMemory(checks);
            CheckParsing(checks);
        }
        finally
        {
            if (string.IsNullOrEmpty(savedSeen)) PlayerPrefs.DeleteKey(RobotInboxSettings.SeenPrefKey);
            else PlayerPrefs.SetString(RobotInboxSettings.SeenPrefKey, savedSeen);
            PlayerPrefs.Save();
        }

        if (checks.Failures.Count == 0)
            return $"PASSED: robot inbox validation ({checks.Count} checks).";

        var report = new StringBuilder();
        report.AppendLine($"FAILED: robot inbox validation ({checks.Failures.Count} of {checks.Count} checks).");
        foreach (string failure in checks.Failures) report.AppendLine("  - " + failure);
        return report.ToString();
    }

    // An item is an arrival, a note, or neither — and the home screen does something different for
    // each. The two must be mutually exclusive: an item counted as both would have its code added AND
    // be written to the seen list, so re-sending a corrected note would silently never show.
    private static void CheckClassification(Checks checks)
    {
        RobotInboxService.Item arrival = Arrival("654V Claw", "654V-8213");
        checks.That(RobotInboxService.IsArrival(arrival), "An item with a code is not an arrival.");
        checks.That(!RobotInboxService.IsNote(arrival), "An item with a code is also counted a note.");

        RobotInboxService.Item note = Note("654V Claw", "The arm came in as one solid piece.");
        checks.That(RobotInboxService.IsNote(note), "An item with only a message is not a note.");
        checks.That(!RobotInboxService.IsArrival(note), "A message-only item is counted an arrival.");

        // An arrival that ALSO carries a message stays an arrival: the code is the part that acts.
        RobotInboxService.Item both = Arrival("654V Claw", "654V-8213");
        both.message = "The intake is simplified.";
        checks.That(RobotInboxService.IsArrival(both), "An arrival with a note lost its arrival status.");
        checks.That(!RobotInboxService.IsNote(both), "An arrival with a note is double-counted.");

        // Neither. A blank item must be dropped, not shown as an empty banner.
        RobotInboxService.Item blank = new RobotInboxService.Item();
        checks.That(!RobotInboxService.IsArrival(blank), "An empty item counted as an arrival.");
        checks.That(!RobotInboxService.IsNote(blank), "An empty item counted as a note.");

        // Whitespace is not content: a code of "  " would be normalized away to nothing, and a
        // message of "\n" would show an empty banner with a Got it button under it.
        checks.That(!RobotInboxService.IsArrival(Arrival("x", "   ")), "A blank code counted as an arrival.");
        checks.That(!RobotInboxService.IsNote(Note("x", " \n ")), "A blank message counted as a note.");
        checks.That(!RobotInboxService.IsArrival(null), "null counted as an arrival.");
        checks.That(!RobotInboxService.IsNote(null), "null counted as a note.");
    }

    private static void CheckKeys(Checks checks)
    {
        // An explicit id wins and is used verbatim (trimmed), so a message can be rewritten without
        // re-showing it — the escape hatch from the fingerprint rule below.
        RobotInboxService.Item identified = Note("654V Claw", "anything", " claw-2026-08 ");
        checks.That(RobotInboxService.KeyFor(identified) == "claw-2026-08",
            $"An explicit id is not the key: got '{RobotInboxService.KeyFor(identified)}'.");

        // Same text, same key: this is what makes a dismissal stick across launches.
        string first = RobotInboxService.KeyFor(Note("654V Claw", "The arm is one piece."));
        string again = RobotInboxService.KeyFor(Note("654V Claw", "The arm is one piece."));
        checks.That(first == again, "The same note fingerprints differently on a second call.");

        // Different text, different key: a REWRITTEN note must show again, or a correction to a
        // player who is waiting on one would land nowhere.
        string rewritten = RobotInboxService.KeyFor(Note("654V Claw", "The arm is one piece. Re-export it."));
        checks.That(first != rewritten, "A rewritten note keeps the key of the old one.");

        // The robot name is part of the key too — the same sentence about two different robots is
        // two different things to say.
        string otherRobot = RobotInboxService.KeyFor(Note("654V Lift", "The arm is one piece."));
        checks.That(first != otherRobot, "The robot name does not affect the fingerprint.");

        // PINNED BYTES. The point of the key is that it survives a relaunch, which no same-process
        // assertion can demonstrate — so the value itself is nailed down. If this line fails, the
        // hash changed, and every note already dismissed on every device comes back at once.
        checks.That(first == "cc23b410",
            $"The fingerprint of a known note changed: got '{first}', expected 'cc23b410'. " +
            "Every note already dismissed on a player's device will reappear.");

        checks.That(RobotInboxService.KeyFor(null) == string.Empty, "KeyFor(null) is not empty.");
    }

    private static void CheckSeenMemory(Checks checks)
    {
        RobotInboxSettings.Forget();

        RobotInboxService.Item note = Note("654V Claw", "The arm came in as one solid piece.");
        string key = RobotInboxService.KeyFor(note);

        checks.That(!RobotInboxSettings.HasSeen(key), "A never-seen note reports as seen.");
        checks.That(RobotInboxSettings.MarkSeen(key), "Marking a fresh note seen reported no change.");
        checks.That(RobotInboxSettings.HasSeen(key), "A note marked seen does not report as seen.");
        checks.That(!RobotInboxSettings.MarkSeen(key), "Marking the same note twice reported a change.");

        // A second note is independent: dismissing one must not dismiss the other.
        string otherKey = RobotInboxService.KeyFor(Note("654V Lift", "Different problem entirely."));
        checks.That(!RobotInboxSettings.HasSeen(otherKey), "Dismissing one note dismissed another.");
        RobotInboxSettings.MarkSeen(otherKey);
        checks.That(RobotInboxSettings.HasSeen(key) && RobotInboxSettings.HasSeen(otherKey),
            "The second note overwrote the first instead of being added.");

        // Blank keys are never stored: an item with no id and no text would otherwise bank one empty
        // key that then matches every other empty key.
        checks.That(!RobotInboxSettings.MarkSeen(string.Empty), "A blank key was stored.");
        checks.That(!RobotInboxSettings.HasSeen(string.Empty), "A blank key reports as seen.");

        // A corrupt pref re-shows an old notice rather than throwing on the launch path.
        PlayerPrefs.SetString(RobotInboxSettings.SeenPrefKey, "{not json at all");
        checks.That(!RobotInboxSettings.HasSeen(key), "A corrupt pref did not fall back to empty.");
        checks.That(RobotInboxSettings.MarkSeen(key), "A corrupt pref could not be written over.");

        RobotInboxSettings.Forget();
        checks.That(!RobotInboxSettings.HasSeen(key), "Forget left the note marked seen.");
    }

    // The inbox file is hand-written from the Firebase console, so its JSON is the one input here
    // with no compiler behind it. These are the exact shapes Docs/Robot-Submissions.md tells you to
    // write; if JsonUtility stops reading them, that document becomes wrong instructions.
    private static void CheckParsing(Checks checks)
    {
        const string json =
            "{\"items\":[" +
            "{\"robotName\":\"654V Claw\",\"code\":\"654V-8213\",\"message\":\"\"}," +
            "{\"id\":\"claw-2026-08\",\"robotName\":\"654V Claw\"," +
            "\"message\":\"The arm came in as one solid piece.\"}" +
            "]}";

        RobotInboxService.Inbox inbox = JsonUtility.FromJson<RobotInboxService.Inbox>(json);
        checks.That(inbox != null && inbox.items != null && inbox.items.Count == 2,
            "The documented inbox JSON did not parse into two items.");
        if (inbox?.items == null || inbox.items.Count != 2) return;

        checks.That(RobotInboxService.IsArrival(inbox.items[0]),
            "The documented arrival item did not read as an arrival.");
        checks.That(inbox.items[0].code == "654V-8213", "The arrival's code did not round-trip.");
        checks.That(RobotInboxService.IsNote(inbox.items[1]),
            "The documented note item did not read as a note.");
        checks.That(RobotInboxService.KeyFor(inbox.items[1]) == "claw-2026-08",
            "The note's id did not become its key.");

        // A file written before `id` existed still parses — JsonUtility leaves the missing field
        // null, and KeyFor falls through to the fingerprint.
        RobotInboxService.Inbox old = JsonUtility.FromJson<RobotInboxService.Inbox>(
            "{\"items\":[{\"robotName\":\"654V Claw\",\"code\":\"654V-8213\",\"message\":\"\"}]}");
        checks.That(old != null && old.items.Count == 1 && RobotInboxService.IsArrival(old.items[0]),
            "An inbox file written before the `id` field stopped parsing.");
    }
}
