using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;

namespace ApocalypterTalk
{
    /// <summary>
    /// The file work behind the "Dialogue Editor" section of the config (shown in the Apocasetter Mods menu):
    /// add a prompt, add a line to a pool, and find / replace / delete an existing line.
    ///
    /// Everything players ADD goes into  BepInEx/config/Apocatalker/my_dialogue.txt  so that mod updates never overwrite it.
    /// Edits to existing lines change the file that contains the line (a .bak copy is made first).
    /// </summary>
    internal static class DialogueEditor
    {
        static string userDirectory;
        public static string UserDirectory
        {
            get
            {
                if (userDirectory == null) userDirectory = GetUserDirectory();
                return userDirectory;
            }
        }
        public static string UserFile { get { return Path.Combine(UserDirectory, "my_dialogue.txt"); } }

        static string GetUserDirectory()
        {
            string current = Path.Combine(Paths.ConfigPath, "Apocatalker");
            string legacy = Path.Combine(Paths.ConfigPath, "ApocalypterTalk");
            if (!Directory.Exists(legacy)) return current;

            try
            {
                if (!Directory.Exists(current))
                {
                    Directory.Move(legacy, current);
                    if (Plugin.Log != null) Plugin.Log.LogInfo("Dialogue: moved user files to config\\Apocatalker.");
                    return current;
                }

                // Merge missing files only; preserve both copies if a destination already exists.
                foreach (string source in Directory.GetFiles(legacy, "*", SearchOption.AllDirectories))
                {
                    string relative = source.Substring(legacy.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string destination = Path.Combine(current, relative);
                    if (File.Exists(destination)) continue;
                    string destinationDirectory = Path.GetDirectoryName(destination);
                    if (!Directory.Exists(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);
                    File.Copy(source, destination);
                }
                if (Plugin.Log != null) Plugin.Log.LogInfo("Dialogue: copied non-conflicting legacy files to config\\Apocatalker; the old folder was kept as a backup.");
            }
            catch (Exception e)
            {
                if (Plugin.Log != null) Plugin.Log.LogWarning("Dialogue: could not fully migrate config\\ApocalypterTalk to config\\Apocatalker: " + e.Message);
            }
            return current;
        }

        static readonly string[] UserFileHeader =
        {
            "# My own dialogue. Created and extended from the in-game Dialogue Editor (Apocasetter Mods menu).",
            "# Same format as dialogue.txt (see HOW_TO_ADD_DIALOGUE.txt). Lives in BepInEx/config, so mod updates never touch it.",
            ""
        };

        sealed class LineMatch
        {
            public string FilePath;
            public int Index;
            public string Text;
        }

        // ------------------------------------------------------------------
        // Adding
        // ------------------------------------------------------------------
        public static bool AddPrompt(string text, string reply, string npc, out string message)
        {
            text = OneLine(text);
            reply = OneLine(reply);
            npc = OneLine(npc);

            if (text.Length == 0) { message = "Not added: 'New prompt - what the player says' is empty."; return false; }
            if (reply.Length == 0) { message = "Not added: 'New prompt - NPC reply' is empty."; return false; }

            try
            {
                string newline; bool trailing;
                List<string> lines = ReadOrCreateUserFile(out newline, out trailing);
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                lines.Add("[prompt]");
                lines.Add("text  = " + text);
                if (npc.Length > 0) lines.Add("npc   = " + npc);
                lines.Add("reply = " + reply);
                WriteLines(UserFile, lines, newline, true);

                message = "Added prompt \"" + Shorten(text, 45) + "\" to my_dialogue.txt.";
                return true;
            }
            catch (Exception e)
            {
                message = "Could not save my_dialogue.txt: " + e.Message;
                return false;
            }
        }

        public static bool AddPoolLine(string pool, string text, out string message)
        {
            pool = NormalisePoolName(pool);
            text = OneLine(text);

            if (pool.Length == 0) { message = "Not added: the pool name is empty (for example: rumors)."; return false; }
            if (pool == "prompt" || pool == "intro") { message = "Not added: 'prompt' and 'intro' are reserved words. Pick another pool name."; return false; }
            if (text.Length == 0) { message = "Not added: 'New line' is empty."; return false; }
            if (text[0] == '#' || text[0] == '[') { message = "Not added: a line can't start with # or [."; return false; }

            try
            {
                string newline; bool trailing;
                List<string> lines = ReadOrCreateUserFile(out newline, out trailing);

                int header = lines.FindLastIndex(l => l.Trim().ToLowerInvariant() == "[" + pool + "]");
                if (header < 0)
                {
                    if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                    lines.Add("[" + pool + "]");
                    lines.Add(text);
                }
                else
                {
                    // put it at the end of that section, before any blank lines that follow it
                    int end = header + 1;
                    while (end < lines.Count && !IsHeader(lines[end])) end++;
                    int insertAt = end;
                    while (insertAt > header + 1 && lines[insertAt - 1].Trim().Length == 0) insertAt--;
                    lines.Insert(insertAt, text);
                }
                WriteLines(UserFile, lines, newline, true);

                message = "Added a line to [" + pool + "] in my_dialogue.txt.";
                return true;
            }
            catch (Exception e)
            {
                message = "Could not save my_dialogue.txt: " + e.Message;
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Editing an existing line
        // ------------------------------------------------------------------
        /// <summary>
        /// Finds the single line (in the given files) that contains <paramref name="find"/>, then replaces or deletes it.
        /// Inside a [prompt] or [intro] block a "key = value" line keeps its key: only the value is replaced.
        /// </summary>
        public static bool EditLine(IEnumerable<string> files, string find, string replacement, bool delete, out string message)
        {
            find = OneLine(find);
            replacement = OneLine(replacement);

            if (find.Length < 3) { message = "Type at least 3 characters of the line in 'Find'."; return false; }
            if (!delete && replacement.Length == 0) { message = "'Replace with' is empty. Use 'Delete line' to remove a line."; return false; }

            string needle = Fold(find);
            var matches = new List<LineMatch>();

            foreach (string path in files)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    string newline; bool trailing;
                    List<string> lines = ReadLines(path, out newline, out trailing);
                    for (int i = 0; i < lines.Count; i++)
                    {
                        string t = lines[i].Trim();
                        if (t.Length == 0 || t[0] == '#' || IsHeader(t)) continue;
                        if (Fold(t).Contains(needle))
                            matches.Add(new LineMatch { FilePath = path, Index = i, Text = t });
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Dialogue editor: could not read " + path + ": " + e.Message);
                }
            }

            if (matches.Count == 0)
            {
                message = "No line contains \"" + Shorten(find, 40) + "\".";
                return false;
            }
            if (matches.Count > 1)
            {
                message = matches.Count + " lines contain that text. Type more of the line so only one matches. First: \"" + Shorten(matches[0].Text, 40) + "\"";
                return false;
            }

            LineMatch m = matches[0];
            string fileName = Path.GetFileName(m.FilePath);
            try
            {
                string newline; bool trailing;
                List<string> lines = ReadLines(m.FilePath, out newline, out trailing);
                string old = lines[m.Index];
                string oldTrimmed = old.Trim();

                if (delete)
                {
                    File.Copy(m.FilePath, m.FilePath + ".bak", true);
                    lines.RemoveAt(m.Index);
                    WriteLines(m.FilePath, lines, newline, trailing);
                    message = "Deleted from " + fileName + ": \"" + Shorten(oldTrimmed, 50) + "\"";
                    return true;
                }

                string indent = old.Substring(0, old.Length - old.TrimStart().Length);
                int eq = oldTrimmed.IndexOf('=');
                bool keepKey = InBlock(lines, m.Index) && eq > 0;

                if (!keepKey && (replacement[0] == '#' || replacement[0] == '['))
                {
                    message = "Not changed: a pool line can't start with # or [.";
                    return false;
                }

                // keep the key exactly as written (including any alignment spaces before the =)
                string newText = keepKey
                    ? indent + oldTrimmed.Substring(0, eq + 1) + " " + replacement
                    : indent + replacement;

                File.Copy(m.FilePath, m.FilePath + ".bak", true);
                lines[m.Index] = newText;
                WriteLines(m.FilePath, lines, newline, trailing);

                message = "Changed a line in " + fileName + ": \"" + Shorten(oldTrimmed, 30) + "\" -> \"" + Shorten(replacement, 30) + "\"";
                return true;
            }
            catch (Exception e)
            {
                message = "Could not edit " + fileName + ": " + e.Message;
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        static List<string> ReadOrCreateUserFile(out string newline, out bool trailing)
        {
            Directory.CreateDirectory(UserDirectory);
            if (!File.Exists(UserFile))
            {
                newline = "\r\n";
                trailing = true;
                return new List<string>(UserFileHeader);
            }
            return ReadLines(UserFile, out newline, out trailing);
        }

        /// <summary>Reads a file keeping track of its line-ending style, so writing it back doesn't disturb the rest of the file.</summary>
        static List<string> ReadLines(string path, out string newline, out bool trailing)
        {
            string raw = File.ReadAllText(path, Encoding.UTF8);
            newline = raw.Contains("\r\n") ? "\r\n" : "\n";
            trailing = raw.EndsWith("\n");
            var lines = new List<string>(raw.Replace("\r\n", "\n").Split('\n'));
            if (trailing && lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }

        static void WriteLines(string path, List<string> lines, string newline, bool trailing)
        {
            string text = string.Join(newline, lines.ToArray()) + (trailing ? newline : "");
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        static bool IsHeader(string line)
        {
            string t = line.Trim();
            return t.Length >= 2 && t[0] == '[' && t[t.Length - 1] == ']';
        }

        /// <summary>True when the line at <paramref name="index"/> sits inside a [prompt] or [intro] block.</summary>
        static bool InBlock(List<string> lines, int index)
        {
            for (int j = index - 1; j >= 0; j--)
            {
                if (!IsHeader(lines[j])) continue;
                string h = lines[j].Trim().ToLowerInvariant();
                return h == "[prompt]" || h == "[intro]";
            }
            return false;
        }

        static string OneLine(string s)
        {
            return (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        /// <summary>Lower case, curly quotes made straight, runs of spaces collapsed: so "Heat's" matches "Heat’s".</summary>
        static string Fold(string s)
        {
            s = (s ?? "").Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u201C', '"').Replace('\u201D', '"');
            var sb = new StringBuilder();
            bool pendingSpace = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        static string NormalisePoolName(string s)
        {
            s = OneLine(s).Trim('[', ']', ' ');
            var sb = new StringBuilder();
            foreach (char c in s.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
                else if (c == ' ') sb.Append('_');
            }
            return sb.ToString();
        }

        static string Shorten(string s, int max)
        {
            s = s ?? "";
            return s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 3)) + "...";
        }
    }
}
