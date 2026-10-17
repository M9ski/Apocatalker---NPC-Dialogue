using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ApocalypterTalk
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        // Permanent plugin ID: other mods and config files depend on it, so never change it.
        public const string PluginGuid = "com.example.apocalypter.talk";
        public const string PluginName = "Apocatalker";
        // Must match the GitHub release tag (tag "v2.0.0") so Apocasetter can offer updates.
        public const string PluginVersion = "2.0.0";

        internal static ManualLogSource Log;
        internal static bool Talking;
        internal static Plugin Instance;
        internal bool Ready;

        // ---- config ----
        ConfigEntry<KeyCode> talkKey;
        ConfigEntry<KeyCode> dumpKey;
        ConfigEntry<bool> logDialogueUi;
        ConfigEntry<KeyCode> reloadKey;
        ConfigEntry<float> talkRange;
        ConfigEntry<string> npcTypeName;
        ConfigEntry<string> npcNameKeywords;
        ConfigEntry<string> hostileKeywords;
        ConfigEntry<string> lookScriptKeywords;
        ConfigEntry<bool> pauseWhileTalking;
        ConfigEntry<bool> loadPackFiles;
        ConfigEntry<bool> saveStoryProgress;
        ConfigEntry<string> storyProfile;
        ConfigEntry<float> windowWidth;
        ConfigEntry<float> windowHeight;
        ConfigEntry<bool> enableCustomTheme;
        ConfigEntry<string> windowColorCfg;
        ConfigEntry<string> buttonColorCfg;
        ConfigEntry<string> textColorCfg;
        ConfigEntry<string> accentColorCfg;
        ConfigEntry<string> outlineColorCfg;
        ConfigEntry<string> windowBorderCfg;
        ConfigEntry<string> windowPaddingCfg;
        ConfigEntry<string> buttonBorderCfg;
        ConfigEntry<float> chatOutline;

        // dialogue editor (these show up in the Apocasetter Mods menu)
        ConfigEntry<string> lastResult, newPromptText, newPromptReply, newPromptNpc, newLinePool, newLineText, findText, replaceWith;
        ConfigEntry<bool> addPromptNow, addLineNow, replaceLineNow, deleteLineNow, reloadNow;
        bool actAddPrompt, actAddLine, actReplace, actDelete, actReload;   // set by the menu, handled in Update

        // ---- runtime state ----
        enum State { Idle, Prompt, Talking }
        State state = State.Idle;

        Type npcType;
        string[] npcKeywords = new string[0];
        string[] hostileWords = new string[0];
        string[] lookWords = new string[0];

        Transform currentNpc;
        string npcName = "";                           // cleaned name, e.g. "Bolter"
        string npcRaw = "";                            // object name, e.g. "Bolter(Clone)1320"
        string[] npcNames = new string[0];             // normalised names used for matching [prompt] npc = ...


        readonly Collider[] buffer = new Collider[128];
        readonly List<Behaviour> disabledLook = new List<Behaviour>();
        float nextScan;
        Camera cam;
        bool inputBroken;
        bool sawFirstUpdate;

        CursorLockMode prevLock;
        bool prevCursorVisible;
        float prevTimeScale = 1f;

        DialogueUI dialogueUI;

        // ---- dialogue ----
        DialogueDb db = new DialogueDb();
        List<PromptDef> choices = new List<PromptDef>();   // what the player can currently pick
        PromptDef pendingPrompt;                           // button actions are handled in Update
        bool pendingClose;

        void Awake()
        {
            Instance = this;
            Log = Logger;
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            Harmony.CreateAndPatchAll(typeof(InputPatches), PluginGuid);

            // Makes this mod appear, with all its settings, in the Apocasetter in-game Mods menu.
            // (Harmless if Apocasetter isn't installed.)
            Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");

            // ---- General ----
            talkKey = Config.Bind("General", "TalkKey", KeyCode.F,
                "Key you press to start a conversation while the \"Press [key] to talk\" prompt is on screen.");
            talkRange = Config.Bind("General", "TalkRange", 3.5f, new ConfigDescription(
                "How close you need to be to an NPC, and facing them, before the talk prompt appears (in metres).",
                new AcceptableValueRange<float>(1f, 10f)));
            pauseWhileTalking = Config.Bind("General", "PauseWhileTalking", true,
                "Freeze the game world while the dialogue window is open.");

            // ---- Dialogue ----
            reloadKey = Config.Bind("Dialogue", "ReloadKey", KeyCode.F10,
                "Press this to re-read all dialogue files without restarting the game (handy after editing the .txt files).");
            loadPackFiles = Config.Bind("Dialogue", "LoadPackFiles", true,
                "Also load every *.talk.txt file found under BepInEx/plugins. This is how other mods add their own NPC dialogue and quests.");
            saveStoryProgress = Config.Bind("Dialogue", "SaveStoryProgress", true,
                "Remember quest and story progress between game sessions. It is stored in the BepInEx/config folder.");
            storyProfile = Config.Bind("Dialogue", "StoryProfile", "",
                "Name of the story-progress file. Use a different name for each save game to keep separate progress. Leave empty for the default.");

            // ---- Dialogue Editor (buttons and fields for the Apocasetter Mods menu) ----
            lastResult = Config.Bind("Dialogue Editor", "LastResult", "Ready.",
                "Shows what your last editor action did. The mod writes this; you don't need to change it.");
            newPromptText = Config.Bind("Dialogue Editor", "NewPromptText", "",
                "ADD A PROMPT, step 1: what the player says. This becomes a button in the dialogue window.");
            newPromptReply = Config.Bind("Dialogue Editor", "NewPromptReply", "",
                "ADD A PROMPT, step 2: how the NPC answers. Write pool:rumors instead to answer with a random line from the rumors pool.");
            newPromptNpc = Config.Bind("Dialogue Editor", "NewPromptNpc", "",
                "ADD A PROMPT, step 3 (optional): only these NPCs offer it, for example Bolter, Lugger. Leave empty for every NPC.");
            addPromptNow = Config.Bind("Dialogue Editor", "AddPromptNow", false,
                "ADD A PROMPT, step 4: switch ON to save the prompt. It is added to your own file (BepInEx/config/Apocatalker/my_dialogue.txt) and works immediately. Switches itself OFF again.");
            newLinePool = Config.Bind("Dialogue Editor", "NewLinePool", "rumors",
                "ADD A LINE TO A POOL, step 1: the pool name, for example rumors, smalltalk or greetings.");
            newLineText = Config.Bind("Dialogue Editor", "NewLineText", "",
                "ADD A LINE TO A POOL, step 2: the line to add.");
            addLineNow = Config.Bind("Dialogue Editor", "AddLineNow", false,
                "ADD A LINE TO A POOL, step 3: switch ON to save the line. It works immediately. Switches itself OFF again.");
            findText = Config.Bind("Dialogue Editor", "FindText", "",
                "CHANGE OR DELETE A LINE, step 1: type part of the line you mean. It must match exactly one line, so type enough of it.");
            replaceWith = Config.Bind("Dialogue Editor", "ReplaceWith", "",
                "CHANGE A LINE, step 2: the new text. For a line like 'reply = ...' or 'text = ...' only the part after the = is replaced.");
            replaceLineNow = Config.Bind("Dialogue Editor", "ReplaceLineNow", false,
                "CHANGE A LINE, step 3: switch ON to replace the line you found. A .bak copy of the file is made first. Switches itself OFF again.");
            deleteLineNow = Config.Bind("Dialogue Editor", "DeleteLineNow", false,
                "DELETE A LINE: switch ON to delete the line you found (a .bak copy of the file is made first). Switches itself OFF again.");
            reloadNow = Config.Bind("Dialogue Editor", "ReloadNow", false,
                "Switch ON to re-read all dialogue files right now, for example after editing them in a text editor. Switches itself OFF again.");

            // ---- Detection ----
            npcNameKeywords = Config.Bind("Detection", "NpcNameKeywords", "bodyguard,pistoleer,pup,merchant,bolter,lugger,gunnar,mechanic,organicmechanic",
                "Which characters can be talked to. Comma-separated words: any character whose name contains one of them gets the talk prompt. Not case sensitive.");
            hostileKeywords = Config.Bind("Detection", "HostileNameKeywords", "enemy,raider,hostile,zombie,mutant,warlord,bandit,dead",
                "Characters whose name contains one of these words never get the talk prompt (enemies, and corpses via 'dead'). Comma-separated.");
            npcTypeName = Config.Bind("Detection", "NpcComponentType", "",
                "Advanced. Full name of the game's NPC script (for example MyGame.NpcController) to use instead of name keywords. Leave empty unless you know you need it.");

            // ---- Camera ----
            lookScriptKeywords = Config.Bind("Camera", "LookScriptKeywords", "look,mouse",
                "Advanced. While talking, scripts on the player or camera whose name contains one of these words are switched off so the view doesn't turn. The log lists what was disabled.");

            // ---- Theme ----
            windowWidth = Config.Bind("Theme", "WindowWidth", 600f, new ConfigDescription(
                "Width of the dialogue window (in 1080p layout pixels).", new AcceptableValueRange<float>(300f, 1200f)));
            windowHeight = Config.Bind("Theme", "WindowHeight", 420f, new ConfigDescription(
                "Height of the dialogue window (in 1080p layout pixels).", new AcceptableValueRange<float>(250f, 900f)));
            enableCustomTheme = Config.Bind("Theme", "EnableCustomTheme", false,
                "Use the optional window and button images in the theme folder. Off uses a clean, color-configurable TextMeshPro style.");
            windowColorCfg = Config.Bind("Theme", "WindowColor", "#252525F2",
                "Window background color in HEX (#RRGGBB or #RRGGBBAA). The alpha value controls transparency.");
            buttonColorCfg = Config.Bind("Theme", "ButtonColor", "#414141FF",
                "Dialogue button color in HEX (#RRGGBB or #RRGGBBAA).");
            textColorCfg = Config.Bind("Theme", "TextColor", "#F4F4F4FF",
                "Dialogue text, title, and prompt color in HEX (#RRGGBB or #RRGGBBAA).");
            accentColorCfg = Config.Bind("Theme", "AccentColor", "#A7A7A7FF",
                "Highlight and scrollbar handle color in HEX (#RRGGBB or #RRGGBBAA).");
            outlineColorCfg = Config.Bind("Theme", "ChatTextOutlineColor", "#000000FF",
                "Text outline color in HEX (#RRGGBB or #RRGGBBAA).");
            windowBorderCfg = Config.Bind("Theme", "WindowBorder", "16",
                "Size of the non-stretching edge of window.png: one number, or left,right,top,bottom. Used only when EnableCustomTheme is on.");
            windowPaddingCfg = Config.Bind("Theme", "WindowPadding", "24,24,40,24",
                "Space between the window edge and its content: left,right,top,bottom. Only matters if you replace the window image.");
            buttonBorderCfg = Config.Bind("Theme", "ButtonBorder", "8",
                "Size of the non-stretching edge of the button images: one number, or left,right,top,bottom. Used only when EnableCustomTheme is on.");
            chatOutline = Config.Bind("Theme", "ChatTextOutline", 0f, new ConfigDescription(
                "Strength of the TextMeshPro outline. 0 turns it off; the clean default is no outline.",
                new AcceptableValueRange<float>(0f, 3f)));

            // ---- Debug ----
            dumpKey = Config.Bind("Debug", "DumpNearbyKey", KeyCode.F8,
                "Stand next to an NPC and press this. Object names and component types nearby are written to the BepInEx log.");
            logDialogueUi = Config.Bind("Debug", "LogDialogueUI", false,
                "Write dialogue UI geometry, scroll, font, and visible-content diagnostics to LogOutput.log when a conversation opens.");

            // Settings changed in the Mods menu take effect immediately, no restart needed.
            npcNameKeywords.SettingChanged += (s, e) => ApplyDetectionSettings();
            hostileKeywords.SettingChanged += (s, e) => ApplyDetectionSettings();
            npcTypeName.SettingChanged += (s, e) => ApplyDetectionSettings();
            lookScriptKeywords.SettingChanged += (s, e) => ApplyDetectionSettings();
            windowWidth.SettingChanged += (s, e) => ApplyThemeSettings();
            windowHeight.SettingChanged += (s, e) => ApplyThemeSettings();
            enableCustomTheme.SettingChanged += (s, e) => ApplyThemeSettings();
            windowColorCfg.SettingChanged += (s, e) => ApplyThemeSettings();
            buttonColorCfg.SettingChanged += (s, e) => ApplyThemeSettings();
            textColorCfg.SettingChanged += (s, e) => ApplyThemeSettings();
            accentColorCfg.SettingChanged += (s, e) => ApplyThemeSettings();
            outlineColorCfg.SettingChanged += (s, e) => ApplyThemeSettings();
            windowBorderCfg.SettingChanged += (s, e) => ApplyThemeSettings();
            windowPaddingCfg.SettingChanged += (s, e) => ApplyThemeSettings();
            buttonBorderCfg.SettingChanged += (s, e) => ApplyThemeSettings();
            chatOutline.SettingChanged += (s, e) => ApplyThemeSettings();
            saveStoryProgress.SettingChanged += (s, e) => ApplyStorySettings();
            storyProfile.SettingChanged += (s, e) => ApplyStorySettings();
            loadPackFiles.SettingChanged += (s, e) => actReload = true;

            // The editor buttons only raise a flag; the work is done in Update, never in the middle of the menu's drawing.
            addPromptNow.SettingChanged += (s, e) => { if (addPromptNow.Value) actAddPrompt = true; };
            addLineNow.SettingChanged += (s, e) => { if (addLineNow.Value) actAddLine = true; };
            replaceLineNow.SettingChanged += (s, e) => { if (replaceLineNow.Value) actReplace = true; };
            deleteLineNow.SettingChanged += (s, e) => { if (deleteLineNow.Value) actDelete = true; };
            reloadNow.SettingChanged += (s, e) => { if (reloadNow.Value) actReload = true; };

            dialogueUI = new DialogueUI(Path.GetDirectoryName(Info.Location),
                prompt => pendingPrompt = prompt,
                () => pendingClose = true);

            ApplyDetectionSettings();
            ApplyThemeSettings();

            TalkApi.InitFlags(saveStoryProgress.Value ? FlagsFilePath() : null);
            LoadDialogue();
            Ready = true;

            Log.LogInfo(PluginName + " " + PluginVersion + " loaded. Press " + dumpKey.Value + " near an NPC to dump nearby object info, "
                + reloadKey.Value + " to reload dialogue files.");
        }

        // ------------------------------------------------------------------
        // Settings that take effect immediately (changed in the Apocasetter Mods menu)
        // ------------------------------------------------------------------
        void ApplyDetectionSettings()
        {
            npcKeywords = Split(npcNameKeywords.Value);
            hostileWords = Split(hostileKeywords.Value);
            lookWords = Split(lookScriptKeywords.Value);

            npcType = null;
            if (!string.IsNullOrEmpty(npcTypeName.Value))
            {
                npcType = FindType(npcTypeName.Value);
                if (npcType == null || !typeof(Component).IsAssignableFrom(npcType))
                {
                    Log.LogWarning("NpcComponentType '" + npcTypeName.Value + "' not found or not a Component; falling back to name keywords.");
                    npcType = null;
                }
                else Log.LogInfo("Using NPC component type: " + npcType.FullName);
            }
        }

        void ApplyThemeSettings()
        {
            if (dialogueUI != null)
                dialogueUI.ApplyTheme(windowWidth.Value, windowHeight.Value, enableCustomTheme.Value,
                    windowColorCfg.Value, buttonColorCfg.Value, textColorCfg.Value, accentColorCfg.Value,
                    outlineColorCfg.Value, windowBorderCfg.Value, windowPaddingCfg.Value,
                    buttonBorderCfg.Value, chatOutline.Value);
        }

        void ApplyStorySettings()
        {
            TalkApi.InitFlags(saveStoryProgress.Value ? FlagsFilePath() : null);
        }

        // ------------------------------------------------------------------
        // Dialogue editor (buttons in the Apocasetter Mods menu)
        // ------------------------------------------------------------------
        void ProcessEditorActions()
        {
            if (!(actReload || actAddPrompt || actAddLine || actReplace || actDelete)) return;
            string msg;
            bool ok;

            if (actReload)
            {
                actReload = false;
                reloadNow.Value = false;
                ReloadDialogue();
                ReportEditor(true, "Reloaded: " + db.FilesLoaded + " file(s), " + db.Prompts.Count + " prompt(s), "
                    + db.Warnings + " warning(s). Warnings are listed in LogOutput.log.");
            }

            if (actAddPrompt)
            {
                actAddPrompt = false;
                addPromptNow.Value = false;
                ok = DialogueEditor.AddPrompt(newPromptText.Value, newPromptReply.Value, newPromptNpc.Value, out msg);
                if (ok)
                {
                    newPromptText.Value = "";
                    newPromptReply.Value = "";
                    newPromptNpc.Value = "";
                    ReloadDialogue();
                }
                ReportEditor(ok, msg);
            }

            if (actAddLine)
            {
                actAddLine = false;
                addLineNow.Value = false;
                ok = DialogueEditor.AddPoolLine(newLinePool.Value, newLineText.Value, out msg);
                if (ok)
                {
                    newLineText.Value = "";
                    ReloadDialogue();
                }
                ReportEditor(ok, msg);
            }

            if (actReplace || actDelete)
            {
                bool delete = actDelete && !actReplace;
                actReplace = false;
                actDelete = false;
                replaceLineNow.Value = false;
                deleteLineNow.Value = false;
                ok = DialogueEditor.EditLine(EditableFiles(), findText.Value, replaceWith.Value, delete, out msg);
                if (ok)
                {
                    findText.Value = "";
                    replaceWith.Value = "";
                    ReloadDialogue();
                }
                ReportEditor(ok, msg);
            }
        }

        void ReportEditor(bool ok, string message)
        {
            if (ok) Log.LogInfo("Dialogue editor: " + message);
            else Log.LogWarning("Dialogue editor: " + message);
            lastResult.Value = message;
        }

        /// <summary>The files the editor may change: this mod's own files and the player's my_dialogue.txt (never other mods' packs).</summary>
        List<string> EditableFiles()
        {
            var list = new List<string>();
            string pluginDir = Path.GetDirectoryName(Info.Location);
            list.Add(Path.Combine(pluginDir, "dialogue.txt"));
            try
            {
                string dir = Path.Combine(pluginDir, "dialogue");
                if (Directory.Exists(dir))
                    list.AddRange(Directory.GetFiles(dir, "*.txt", SearchOption.AllDirectories)
                        .Where(x => x.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception) { }
            list.AddRange(UserFiles());
            return list.Where(x => File.Exists(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The player's own files in BepInEx/config/Apocatalker.</summary>
        List<string> UserFiles()
        {
            var list = new List<string>();
            try
            {
                string dir = DialogueEditor.UserDirectory;
                if (Directory.Exists(dir))
                    list.AddRange(Directory.GetFiles(dir, "*.txt", SearchOption.AllDirectories)
                        .Where(x => x.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception e)
            {
                Log.LogWarning("Dialogue: could not scan " + DialogueEditor.UserDirectory + ": " + e.Message);
            }
            return list;
        }

        string FlagsFilePath()
        {
            string profile = (storyProfile.Value ?? "").Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) profile = profile.Replace(c, '_');
            string name = "ApocalypterTalk.story" + (profile.Length > 0 ? "." + profile : "") + ".txt";
            return Path.Combine(Paths.ConfigPath, name);
        }

        // ------------------------------------------------------------------
        // Update loop
        // ------------------------------------------------------------------
        void Update()
        {
            ProcessEditorActions();       // buttons clicked in the Apocasetter Mods menu
            if (inputBroken) return;
            if (!sawFirstUpdate) { sawFirstUpdate = true; Log.LogInfo("Update loop is running."); }

            try
            {
                if (Input.GetKeyDown(dumpKey.Value)) DumpNearby();
                if (Input.GetKeyDown(reloadKey.Value)) ReloadDialogue();

                if (state == State.Talking)
                {
                    // UI callbacks queue actions here so conversation state changes happen in Update.
                    if (pendingClose) { pendingClose = false; pendingPrompt = null; Close(); return; }
                    if (pendingPrompt != null)
                    {
                        var chosen = pendingPrompt;
                        pendingPrompt = null;
                        ChoosePrompt(chosen);
                    }

                    if (Input.GetKey(KeyCode.Escape))
                    {
                        InputPatches.ConsumeEscapeForFrame();
                        Close();
                        return;
                    }

                    var c = GetCam();
                    if (currentNpc == null || c == null ||
                        Vector3.Distance(c.transform.position, currentNpc.position) > talkRange.Value + 4f)
                        Close();
                    return;
                }

                // Don't offer conversations while something else (the ESC menu, the Apocasetter Mods window) has paused the game.
                if (Time.timeScale == 0f)
                {
                    state = State.Idle;
                    currentNpc = null;
                    dialogueUI.HidePrompt();
                    return;
                }

                if (Time.unscaledTime >= nextScan)
                {
                    nextScan = Time.unscaledTime + 0.25f;
                    var c = GetCam();
                    currentNpc = c != null ? FindNearbyNpc(c.transform) : null;
                    if (currentNpc != null)
                    {
                        npcName = CleanName(currentNpc.name);
                        state = State.Prompt;
                        dialogueUI.ShowPrompt(npcName, talkKey.Value);
                    }
                    else
                    {
                        state = State.Idle;
                        dialogueUI.HidePrompt();
                    }
                }

                if (state == State.Prompt && currentNpc != null && Input.GetKeyDown(talkKey.Value))
                    Open();
            }
            catch (InvalidOperationException e)
            {
                // Happens if the game only uses the new Input System.
                inputBroken = true;
                Log.LogError("Legacy Input is disabled in this game, so key presses can't be read this way: " + e.Message);
            }
        }

        void LateUpdate()
        {
            if (state != State.Talking) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        void OnDestroy()
        {
            if (state == State.Talking) Close();
            if (dialogueUI != null) dialogueUI.Dispose();
        }

        // ------------------------------------------------------------------
        // NPC detection
        // ------------------------------------------------------------------
        Camera GetCam()
        {
            if (cam != null && cam.isActiveAndEnabled) return cam;
            cam = Camera.main;
            if (cam == null)
                cam = Camera.allCameras.FirstOrDefault(c => c != null && c.isActiveAndEnabled);
            return cam;
        }

        Transform FindNearbyNpc(Transform eye)
        {
            int n = Physics.OverlapSphereNonAlloc(eye.position, talkRange.Value, buffer, ~0, QueryTriggerInteraction.Ignore);
            Transform best = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < n; i++)
            {
                var col = buffer[i];
                if (col == null) continue;

                var npc = ResolveNpc(col);
                if (npc == null || !IsFriendly(npc)) continue;

                Vector3 to = npc.position - eye.position;
                float d = to.magnitude;
                if (d < 0.01f || d > talkRange.Value) continue;
                if (Vector3.Dot(eye.forward, to / d) < 0.5f) continue; // must be roughly in front of you

                if (d < bestDist) { best = npc; bestDist = d; }
            }
            return best;
        }

        Transform ResolveNpc(Collider col)
        {
            if (npcType != null)
            {
                var comp = col.GetComponentInParent(npcType);
                return comp != null ? comp.transform : null;
            }

            var t = col.transform;
            for (int i = 0; i < 5 && t != null; i++, t = t.parent)
                if (ContainsAny(t.name, npcKeywords)) return t;
            return null;
        }

        // Hook point: once you know how the game marks hostile / dead / faction NPCs,
        // add real checks here (e.g. read a faction or health field via the NPC component).
        bool IsFriendly(Transform npc)
        {
            return !ContainsAny(npc.name, hostileWords);
        }

        void DumpNearby()
        {
            var c = GetCam();
            if (c == null) { Log.LogWarning("Dump: no active camera found."); return; }

            Log.LogInfo("Camera hierarchy scripts: " + string.Join(", ",
                c.transform.root.GetComponentsInChildren<MonoBehaviour>()
                    .Where(m => m != null)
                    .Select(m => m.GetType().FullName)
                    .Distinct()
                    .ToArray()));

            int n = Physics.OverlapSphereNonAlloc(c.transform.position, 6f, buffer, ~0, QueryTriggerInteraction.Collide);
            var seen = new HashSet<Transform>();
            Log.LogInfo("---- nearby dump (" + n + " colliders) ----");
            for (int i = 0; i < n; i++)
            {
                if (buffer[i] == null) continue;
                var t = buffer[i].transform;
                // log the object that was hit and its root, since NPC scripts may sit on either
                foreach (var obj in new[] { t, t.root })
                {
                    if (!seen.Add(obj)) continue;
                    var types = obj.GetComponentsInParent<MonoBehaviour>(true)
                        .Where(m => m != null)
                        .Select(m => m.GetType().FullName)
                        .Distinct()
                        .Take(25)
                        .ToArray();
                    Log.LogInfo("[" + obj.name + "] " + string.Join(", ", types));
                }
            }
            Log.LogInfo("---- end dump ----");
        }

        // ------------------------------------------------------------------
        // Open / close
        // ------------------------------------------------------------------
        void Open()
        {
            state = State.Talking;
            Talking = true;
            pendingPrompt = null;
            pendingClose = false;

            npcRaw = currentNpc.name;
            npcNames = new[] { Rules.Norm(npcRaw), Rules.Norm(npcName) };
            TalkApi.CurrentNpc = currentNpc;
            TalkApi.CurrentNpcName = npcName;
            Log.LogInfo("Dialogue: talking to '" + npcName + "' (object name '" + npcRaw + "'). Use either name in 'npc = ...'.");
            dialogueUI.ShowDialogue(npcName);

            prevLock = Cursor.lockState;
            prevCursorVisible = Cursor.visible;
            prevTimeScale = Time.timeScale;
            if (pauseWhileTalking.Value) Time.timeScale = 0f;
            FreezeLook();

            // opening line: a matching [intro] block if there is one, otherwise a random [greetings] line
            var seeded = new System.Random(currentNpc.GetInstanceID());
            string greeting;
            var intro = db.FindIntro(npcNames, seeded);
            if (intro != null)
            {
                greeting = intro.Lines[seeded.Next(intro.Lines.Count)];
                Rules.Apply(intro.Set);
                foreach (string a in intro.Actions) TalkApi.RaiseAction(a, npcName);
            }
            else greeting = db.PickSeeded("greetings", seeded) ?? "Hello.";
            AddNpc(greeting);

            choices = BuildMainList();
            dialogueUI.SetChoices(choices);
            if (logDialogueUi.Value)
                Log.LogInfo(dialogueUI.GetDiagnostics());
            TalkApi.RaiseStarted(npcName);
        }

        void Close()
        {
            if (state != State.Talking) return;
            RestoreLook();
            state = State.Idle;
            Talking = false;
            dialogueUI.HideDialogue();
            dialogueUI.HidePrompt();
            if (pauseWhileTalking.Value) Time.timeScale = prevTimeScale;
            Cursor.lockState = prevLock;
            Cursor.visible = prevCursorVisible;
            currentNpc = null;
            nextScan = Time.unscaledTime + 0.5f;
            TalkApi.CurrentNpc = null;
            TalkApi.CurrentNpcName = "";
            TalkApi.RaiseEnded();
        }

        // Switch off the game's mouse-look script(s) while the window is open.
        void FreezeLook()
        {
            disabledLook.Clear();
            var c = GetCam();
            if (c == null || lookWords.Length == 0) return;

            foreach (var mb in c.transform.root.GetComponentsInChildren<MonoBehaviour>())
            {
                if (mb == null || mb == this || !mb.enabled) continue;
                if (!ContainsAny(mb.GetType().Name, lookWords)) continue;
                mb.enabled = false;
                disabledLook.Add(mb);
                Log.LogInfo("Dialogue: disabled " + mb.GetType().FullName + " on " + mb.gameObject.name);
            }
            if (disabledLook.Count == 0)
                Log.LogInfo("Dialogue: no look script matched. Press F8 and check 'Camera hierarchy scripts' in the log.");
        }

        void RestoreLook()
        {
            foreach (var b in disabledLook)
                if (b != null) b.enabled = true;
            disabledLook.Clear();
        }

        // ------------------------------------------------------------------
        // Conversation logic
        // ------------------------------------------------------------------
        /// <summary>Every prompt that applies to this NPC right now, in display order.</summary>
        List<PromptDef> BuildMainList()
        {
            var list = new List<PromptDef>();
            foreach (var p in db.Prompts)
            {
                if (p.Hidden || p.Text.Length == 0) continue;
                if (p.Once && TalkApi.GetFlag("seen:" + p.Id)) continue;
                if (!Rules.NpcMatches(p.NpcInclude, p.NpcExclude, npcNames)) continue;
                if (!Rules.Pass(p.Conditions)) continue;
                list.Add(p);
            }
            return list.OrderBy(x => x.Order).ToList();   // OrderBy is stable: same Order keeps file order
        }

        /// <summary>The follow-up choices named by a prompt's "next = ..." line.</summary>
        List<PromptDef> BuildBranchList(List<string> ids)
        {
            var list = new List<PromptDef>();
            foreach (string id in ids)
            {
                PromptDef p;
                if (!db.PromptsById.TryGetValue(id, out p)) continue;
                if (p.Text.Length == 0) continue;
                if (p.Once && TalkApi.GetFlag("seen:" + p.Id)) continue;
                if (!Rules.Pass(p.Conditions)) continue;
                list.Add(p);
            }
            return list;
        }

        void ChoosePrompt(PromptDef p)
        {
            AddLine("You: " + p.Text);

            string reply = db.PickReply(p);
            if (!string.IsNullOrEmpty(reply)) AddNpc(reply);

            if (p.Once) TalkApi.SetFlag("seen:" + p.Id, true);
            Rules.Apply(p.Set);
            foreach (string a in p.Actions) TalkApi.RaiseAction(a, npcName);

            // flags may have changed, so work out what the player can say next
            List<PromptDef> next = null;
            if (p.Next.Count > 0)
            {
                var branch = BuildBranchList(p.Next);
                if (branch.Count > 0) next = branch;
            }
            choices = next ?? BuildMainList();
            dialogueUI.SetChoices(choices);
        }

        void AddNpc(string text)
        {
            AddLine(npcName + ": " + text.Replace("{npc}", npcName));
            dialogueUI.ScrollChatToBottom();
        }

        void AddLine(string richText)
        {
            if (state == State.Talking) dialogueUI.AppendChatLine(richText);
        }

        // ------------------------------------------------------------------
        // Dialogue files
        // ------------------------------------------------------------------
        /// <summary>Re-read every dialogue file (also bound to the ReloadKey).</summary>
        internal void ReloadDialogue()
        {
            LoadDialogue();
            pendingPrompt = null;
            if (state == State.Talking)
            {
                choices = BuildMainList();
                dialogueUI.SetChoices(choices);
            }
        }

        void LoadDialogue()
        {
            var fresh = new DialogueDb();
            string pluginDir = Path.GetDirectoryName(Info.Location);

            // 1. the main file
            string main = Path.Combine(pluginDir, "dialogue.txt");
            if (File.Exists(main)) fresh.LoadFile(main);
            else Log.LogWarning("dialogue.txt not found next to the plugin DLL.");
            bool mainHadPrompts = fresh.Prompts.Count > 0;

            // 2. extra files: this mod's dialogue folder, content packs from other mods, files registered through TalkApi
            var files = new List<string>();
            try
            {
                string dir = Path.Combine(pluginDir, "dialogue");
                if (Directory.Exists(dir))
                    files.AddRange(Directory.GetFiles(dir, "*.txt", SearchOption.AllDirectories)
                        .Where(x => x.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)));

                if (loadPackFiles.Value && Directory.Exists(Paths.PluginPath))
                    files.AddRange(Directory.GetFiles(Paths.PluginPath, "*.talk.txt", SearchOption.AllDirectories)
                        .Where(x => x.EndsWith(".talk.txt", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception e)
            {
                Log.LogWarning("Dialogue: could not scan for dialogue files: " + e.Message);
            }
            files.AddRange(TalkApi.ExtraFiles);

            foreach (string f in files.Distinct(StringComparer.OrdinalIgnoreCase)
                                      .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(f, main, StringComparison.OrdinalIgnoreCase)) continue;
                Log.LogInfo("Dialogue: loading " + f);
                fresh.LoadFile(f);
            }

            // 3. the player's own additions from the in-game editor (BepInEx/config/Apocatalker), loaded last so they win
            foreach (string f in UserFiles())
            {
                Log.LogInfo("Dialogue: loading " + f);
                fresh.LoadFile(f);
            }

            // an old-style dialogue.txt (only [rumors] / [smalltalk] pools) still gets its two classic buttons
            if (!mainHadPrompts) fresh.AddLegacyPrompts();

            fresh.Finish();
            fresh.Validate();
            db = fresh;

            Log.LogInfo("Dialogue: " + fresh.FilesLoaded + " file(s), " + fresh.Prompts.Count + " prompt(s), "
                + fresh.Intros.Count + " intro(s), " + fresh.Pools.Count + " line pool(s), " + fresh.Warnings + " warning(s).");
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------
        static string[] Split(string csv)
        {
            return (csv ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToLowerInvariant())
                .Where(s => s.Length > 0)
                .ToArray();
        }

        static bool ContainsAny(string name, string[] words)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            for (int i = 0; i < words.Length; i++)
                if (n.Contains(words[i])) return true;
            return false;
        }

        static string CleanName(string raw)
        {
            string s = raw.Replace("(Clone)", "").Replace("_", " ");
            s = Regex.Replace(s, @"\s*\d+\s*$", "");
            s = Regex.Replace(s, @"(?<=[a-z])(?=[A-Z])", " ");
            s = s.Trim();
            return s.Length == 0 ? "Stranger" : s;
        }

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }
    }

    // While the dialogue window is open, mouse-look axes read as zero so the camera stays put.
    static class InputPatches
    {
        static readonly HashSet<string> seen = new HashSet<string>();
        static int escapeConsumedFrame = -1;

        internal static void ConsumeEscapeForFrame()
        {
            escapeConsumedFrame = Time.frameCount;
        }

        static bool ShouldBlockEscape()
        {
            return Plugin.Talking || escapeConsumedFrame == Time.frameCount;
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetKeyDown), typeof(KeyCode))]
        [HarmonyPrefix]
        static bool GetKeyDownPrefix(KeyCode key, ref bool __result)
        {
            if (key != KeyCode.Escape || !ShouldBlockEscape()) return true;
            __result = false;
            return false;
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetButtonDown), typeof(string))]
        [HarmonyPrefix]
        static bool GetButtonDownPrefix(string buttonName, ref bool __result)
        {
            if (!ShouldBlockEscape() ||
                (!string.Equals(buttonName, "Cancel", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(buttonName, "Pause", StringComparison.OrdinalIgnoreCase))) return true;
            __result = false;
            return false;
        }

        static bool Block(string axis, ref float result)
        {
            if (!Plugin.Talking) return true;

            if (axis != null && seen.Add(axis))
                Plugin.Log.LogInfo("Dialogue: axis requested while talking: " + axis);

            string a = (axis ?? "").ToLowerInvariant();
            if (a.Contains("mouse") || a.Contains("look"))
            {
                result = 0f;
                return false; // skip the real call
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetAxis), typeof(string))]
        [HarmonyPrefix]
        static bool GetAxisPrefix(string axisName, ref float __result)
        {
            return Block(axisName, ref __result);
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetAxisRaw), typeof(string))]
        [HarmonyPrefix]
        static bool GetAxisRawPrefix(string axisName, ref float __result)
        {
            return Block(axisName, ref __result);
        }
    }
}
