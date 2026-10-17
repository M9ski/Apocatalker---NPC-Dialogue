using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ApocalypterTalk
{
    /// <summary>
    /// Hooks for OTHER BepInEx mods (quest mods, shop mods, ...). Reference ApocalypterTalk.dll from your own plugin
    /// and add [BepInDependency("com.example.apocalypter.talk")] so this mod loads first.
    ///
    /// Typical use:
    ///   TalkApi.LoadFile(path to your MyQuest.talk.txt);                // or just drop the file under BepInEx/plugins
    ///   TalkApi.ActionTriggered += (action, npc) => { if (action == "quest.start:lugger") ... };
    ///   TalkApi.SetFlag("has_lugger_wrenches");                         // lets dialogue react to what the player did
    /// </summary>
    public static class TalkApi
    {
        static readonly HashSet<string> flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string flagsPath;                                           // null = progress is not saved to disk
        static readonly List<string> extraFiles = new List<string>();

        // ------------------------------------------------------------------
        // Events
        // ------------------------------------------------------------------
        /// <summary>Raised when a prompt or intro with "action = ..." runs. Arguments: the action text exactly as written, and the NPC's name.</summary>
        public static event Action<string, string> ActionTriggered;

        /// <summary>Raised when the dialogue window opens. Argument: the NPC's name.</summary>
        public static event Action<string> ConversationStarted;

        /// <summary>Raised when the dialogue window closes.</summary>
        public static event Action ConversationEnded;

        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------
        /// <summary>True while the dialogue window is open.</summary>
        public static bool IsTalking { get { return Plugin.Talking; } }

        /// <summary>Cleaned-up name of the NPC being talked to (empty when nobody is).</summary>
        public static string CurrentNpcName { get; internal set; }

        /// <summary>The NPC's transform while talking (null otherwise).</summary>
        public static Transform CurrentNpc { get; internal set; }

        // ------------------------------------------------------------------
        // Flags (the memory of the story)
        // ------------------------------------------------------------------
        public static bool GetFlag(string name)
        {
            return !string.IsNullOrEmpty(name) && flags.Contains(name.Trim());
        }

        public static void SetFlag(string name, bool value = true)
        {
            if (string.IsNullOrEmpty(name)) return;
            name = name.Trim();
            bool changed = value ? flags.Add(name) : flags.Remove(name);
            if (changed) SaveFlags();
        }

        public static IEnumerable<string> AllFlags()
        {
            return flags.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static void ClearAllFlags()
        {
            flags.Clear();
            SaveFlags();
        }

        // ------------------------------------------------------------------
        // Dialogue files
        // ------------------------------------------------------------------
        /// <summary>Load an extra dialogue file (same format as dialogue.txt). It is kept when dialogue is reloaded.</summary>
        public static void LoadFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!extraFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) extraFiles.Add(path);
            if (Plugin.Instance != null && Plugin.Instance.Ready) Plugin.Instance.ReloadDialogue();
        }

        /// <summary>Re-read every dialogue file.</summary>
        public static void Reload()
        {
            if (Plugin.Instance != null && Plugin.Instance.Ready) Plugin.Instance.ReloadDialogue();
        }

        // ------------------------------------------------------------------
        // Internal plumbing
        // ------------------------------------------------------------------
        internal static IEnumerable<string> ExtraFiles { get { return extraFiles.ToArray(); } }

        internal static void InitFlags(string path)
        {
            flagsPath = path;
            flags.Clear();
            if (path == null || !File.Exists(path)) return;
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    string s = line.Trim();
                    if (s.Length > 0 && s[0] != '#') flags.Add(s);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not read story progress file " + path + ": " + e.Message);
            }
        }

        static void SaveFlags()
        {
            if (flagsPath == null) return;
            try
            {
                File.WriteAllLines(flagsPath, new[] { "# Story progress for Apocalypter Talk. Delete this file to start over." }
                    .Concat(flags.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)).ToArray());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not save story progress to " + flagsPath + ": " + e.Message);
            }
        }

        internal static void RaiseAction(string action, string npcName)
        {
            Plugin.Log.LogInfo("Dialogue action: " + action + " (npc: " + npcName + ")");
            var handlers = ActionTriggered;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try { ((Action<string, string>)d)(action, npcName); }
                catch (Exception e) { Plugin.Log.LogError("A mod's ActionTriggered handler threw: " + e); }
            }
        }

        internal static void RaiseStarted(string npcName)
        {
            var handlers = ConversationStarted;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try { ((Action<string>)d)(npcName); }
                catch (Exception e) { Plugin.Log.LogError("A mod's ConversationStarted handler threw: " + e); }
            }
        }

        internal static void RaiseEnded()
        {
            var handlers = ConversationEnded;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try { ((Action)d)(); }
                catch (Exception e) { Plugin.Log.LogError("A mod's ConversationEnded handler threw: " + e); }
            }
        }
    }
}
