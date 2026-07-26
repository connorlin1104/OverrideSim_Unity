using System;
using System.Collections.Generic;
using UnityEngine;

// The owner codes entered on THIS device, which is what unlocks private robots in the model picker.
//
// Why codes rather than accounts: teams don't want their designs hole-counted, but the app has no
// backend and no sign-in. A private robot still ships inside the app — it's just filtered out of the
// picker until its owner types the code they were given. That stops casual copying between players;
// it is NOT protection against someone digging through the app's files, and it isn't meant to be.
// Real privacy needs the robot to live on a server and download only after the uploader is verified.
//
// Stored in PlayerPrefs as one JSON string, following the ControllerBindings convention (JsonUtility
// can't serialize a Dictionary or a bare List, so it goes through a small [Serializable] holder).
public static class RobotOwnerSettings
{
    public const string CodesPrefKey = "UnlockedRobotCodes";

    [Serializable]
    private class CodeList
    {
        public List<string> codes = new List<string>();
    }

    // Codes are compared case- and whitespace-insensitively: they get read off a screenshot or a
    // Discord message and typed on a phone keyboard, so "654v-8213 " must match "654V-8213".
    public static string Normalize(string code)
    {
        return string.IsNullOrWhiteSpace(code) ? string.Empty : code.Trim().ToUpperInvariant();
    }

    public static bool HasCode(string code)
    {
        string wanted = Normalize(code);
        if (wanted.Length == 0) return false;

        foreach (string held in Load().codes)
        {
            if (Normalize(held) == wanted) return true;
        }
        return false;
    }

    public static List<string> AllCodes()
    {
        return Load().codes;
    }

    // Returns false when the code is blank or already held, so the UI can say so instead of silently
    // appearing to work.
    public static bool AddCode(string code)
    {
        string normalized = Normalize(code);
        if (normalized.Length == 0) return false;
        if (HasCode(normalized)) return false;

        CodeList list = Load();
        list.codes.Add(normalized);
        Save(list);
        return true;
    }

    public static bool RemoveCode(string code)
    {
        string normalized = Normalize(code);
        CodeList list = Load();
        int removed = list.codes.RemoveAll(held => Normalize(held) == normalized);
        if (removed == 0) return false;

        Save(list);
        return true;
    }

    private static CodeList Load()
    {
        string json = PlayerPrefs.GetString(CodesPrefKey, string.Empty);
        if (string.IsNullOrEmpty(json)) return new CodeList();

        try
        {
            CodeList list = JsonUtility.FromJson<CodeList>(json);
            if (list == null) return new CodeList();
            if (list.codes == null) list.codes = new List<string>();
            return list;
        }
        catch (Exception)
        {
            // A corrupt pref must not lock the player out of the picker entirely — start clean.
            return new CodeList();
        }
    }

    private static void Save(CodeList list)
    {
        PlayerPrefs.SetString(CodesPrefKey, JsonUtility.ToJson(list));
        PlayerPrefs.Save(); // flush now so a force-quit doesn't lose an unlock the player just typed
    }
}
