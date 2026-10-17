using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ApocalypterTalk
{
    /// <summary>One thing the player can say, plus how the NPC answers. Defined by a [prompt] block in a .txt file.</summary>
    internal class PromptDef
    {
        public string Id = "";
        public string Text = "";
        public string Source = "";            // "file.txt line 12", for log messages
        public int Order;                     // lower = higher in the list
        public bool Once;                     // disappears after being chosen once
        public bool Hidden;                   // only reachable through another prompt's "next"
        public bool Sequence;                 // replies are used in order instead of at random
        public int SeqIndex;                  // runtime only

        public readonly List<string> NpcInclude = new List<string>();
        public readonly List<string> NpcExclude = new List<string>();
        public readonly List<string> Conditions = new List<string>();
        public readonly List<string> Set = new List<string>();
        public readonly List<string> Actions = new List<string>();
        public readonly List<string> Replies = new List<string>();
        public readonly List<string> Next = new List<string>();
    }

    /// <summary>What an NPC says when the conversation opens. Defined by an [intro] block.</summary>
    internal class IntroDef
    {
        public string Source = "";
        public int Priority;

        public readonly List<string> NpcInclude = new List<string>();
        public readonly List<string> NpcExclude = new List<string>();
        public readonly List<string> Conditions = new List<string>();
        public readonly List<string> Set = new List<string>();
        public readonly List<string> Actions = new List<string>();
        public readonly List<string> Lines = new List<string>();
    }

    /// <summary>Flag conditions and NPC name matching.</summary>
    internal static class Rules
    {
        /// <summary>Lower-case letters and digits only, so "Rip-Off Merchant", "RipOffMerchant(Clone)12" and "rip off merchant" all compare equal.</summary>
        public static string Norm(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s ?? "")
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        /// <summary>Every condition must hold. "flag" = flag must be set, "!flag" = flag must not be set.</summary>
        public static bool Pass(List<string> conditions)
        {
            for (int i = 0; i < conditions.Count; i++)
            {
                string c = conditions[i];
                bool negate = c.StartsWith("!");
                string name = negate ? c.Substring(1).Trim() : c;
                if (name.Length == 0) continue;
                if (TalkApi.GetFlag(name) == negate) return false;
            }
            return true;
        }

        /// <summary>"flag" sets a flag, "!flag" clears it.</summary>
        public static void Apply(List<string> setList)
        {
            for (int i = 0; i < setList.Count; i++)
            {
                string c = setList[i];
                bool clear = c.StartsWith("!");
                string name = clear ? c.Substring(1).Trim() : c;
                if (name.Length == 0) continue;
                TalkApi.SetFlag(name, !clear);
            }
        }

        /// <summary>Empty include list = every NPC. Any exclude match always wins.</summary>
        public static bool NpcMatches(List<string> include, List<string> exclude, string[] names)
        {
            for (int i = 0; i < exclude.Count; i++)
                if (AnyContains(names, exclude[i])) return false;
            if (include.Count == 0) return true;
            for (int i = 0; i < include.Count; i++)
                if (AnyContains(names, include[i])) return true;
            return false;
        }

        static bool AnyContains(string[] names, string token)
        {
            for (int i = 0; i < names.Length; i++)
                if (names[i].Length > 0 && names[i].Contains(token)) return true;
            return false;
        }
    }

    /// <summary>Everything loaded from the dialogue text files.</summary>
    internal class DialogueDb
    {
        public readonly Dictionary<string, List<string>> Pools = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public readonly List<PromptDef> Prompts = new List<PromptDef>();
        public readonly Dictionary<string, PromptDef> PromptsById = new Dictionary<string, PromptDef>(StringComparer.OrdinalIgnoreCase);
        public readonly List<IntroDef> Intros = new List<IntroDef>();
        public int FilesLoaded;
        public int Warnings;

        readonly Dictionary<string, Queue<string>> bags = new Dictionary<string, Queue<string>>();
        readonly System.Random rng = new System.Random();
        int autoCounter;

        void Warn(string loc, string message)
        {
            Warnings++;
            Plugin.Log.LogWarning("Dialogue: " + loc + ": " + message);
        }

        // ------------------------------------------------------------------
        // Reading a file
        // ------------------------------------------------------------------
        public void LoadFile(string path)
        {
            string[] all;
            try { all = File.ReadAllLines(path, Encoding.UTF8); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Dialogue: could not read " + path + ": " + e.Message);
                return;
            }

            FilesLoaded++;
            string file = Path.GetFileName(path);
            string pool = null;
            PromptDef prompt = null;
            IntroDef intro = null;

            for (int i = 0; i < all.Length; i++)
            {
                string t = all[i].Trim();
                if (t.Length == 0 || t[0] == '#') continue;
                int lineNo = i + 1;
                string loc = file + " line " + lineNo;

                // [section header]
                if (t[0] == '[' && t[t.Length - 1] == ']')
                {
                    string name = t.Substring(1, t.Length - 2).Trim().ToLowerInvariant();
                    pool = null; prompt = null; intro = null;

                    if (name == "prompt")
                    {
                        prompt = new PromptDef { Source = loc };
                        Prompts.Add(prompt);
                    }
                    else if (name == "intro")
                    {
                        intro = new IntroDef { Source = loc };
                        Intros.Add(intro);
                    }
                    else
                    {
                        // anything else is a plain pool of lines, e.g. [rumors]
                        pool = name;
                        if (!Pools.ContainsKey(pool)) Pools[pool] = new List<string>();
                    }
                    continue;
                }

                if (pool != null)
                {
                    Pools[pool].Add(Unescape(t));
                    continue;
                }

                if (prompt == null && intro == null)
                {
                    Warn(loc, "text found before any [section] header; ignored.");
                    continue;
                }

                int eq = t.IndexOf('=');
                if (eq <= 0)
                {
                    Warn(loc, "expected 'key = value' but found: " + t);
                    continue;
                }
                string key = t.Substring(0, eq).Trim().ToLowerInvariant();
                string val = t.Substring(eq + 1).Trim();

                if (prompt != null) ReadPromptKey(prompt, key, val, loc);
                else ReadIntroKey(intro, key, val, loc);
            }
        }

        void ReadPromptKey(PromptDef p, string key, string val, string loc)
        {
            switch (key)
            {
                case "id": p.Id = val; break;
                case "text": p.Text = val; break;
                case "reply": if (val.Length > 0) p.Replies.Add(Unescape(val)); break;
                case "npc": ParseNpc(val, p.NpcInclude, p.NpcExclude); break;
                case "if": AddList(val, p.Conditions); break;
                case "set": AddList(val, p.Set); break;
                case "action": if (val.Length > 0) p.Actions.Add(val); break;
                case "next": AddList(val, p.Next); break;
                case "once": p.Once = ParseBool(val); break;
                case "hidden": p.Hidden = ParseBool(val); break;
                case "mode": p.Sequence = val.Trim().ToLowerInvariant() == "sequence"; break;
                case "order":
                    {
                        int o;
                        if (int.TryParse(val, out o)) p.Order = o;
                        else Warn(loc, "'order' must be a whole number.");
                        break;
                    }
                default:
                    Warn(loc, "unknown key '" + key + "' in a [prompt] block (ignored).");
                    break;
            }
        }

        void ReadIntroKey(IntroDef d, string key, string val, string loc)
        {
            switch (key)
            {
                case "line":
                case "reply": if (val.Length > 0) d.Lines.Add(Unescape(val)); break;
                case "npc": ParseNpc(val, d.NpcInclude, d.NpcExclude); break;
                case "if": AddList(val, d.Conditions); break;
                case "set": AddList(val, d.Set); break;
                case "action": if (val.Length > 0) d.Actions.Add(val); break;
                case "priority":
                    {
                        int pr;
                        if (int.TryParse(val, out pr)) d.Priority = pr;
                        else Warn(loc, "'priority' must be a whole number.");
                        break;
                    }
                default:
                    Warn(loc, "unknown key '" + key + "' in an [intro] block (ignored).");
                    break;
            }
        }

        static string Unescape(string s)
        {
            return s.Replace("\\n", "\n");
        }

        static bool ParseBool(string v)
        {
            string s = v.Trim().ToLowerInvariant();
            return s == "true" || s == "yes" || s == "1" || s == "on";
        }

        static void AddList(string val, List<string> target)
        {
            foreach (string part in val.Split(','))
            {
                string s = part.Trim();
                if (s.Length > 0) target.Add(s);
            }
        }

        static void ParseNpc(string val, List<string> include, List<string> exclude)
        {
            foreach (string raw in val.Split(','))
            {
                string tok = raw.Trim();
                if (tok.Length == 0 || tok == "*") continue;
                bool negate = tok[0] == '!';
                string n = Rules.Norm(negate ? tok.Substring(1) : tok);
                if (n.Length == 0) continue;
                if (negate) exclude.Add(n); else include.Add(n);
            }
        }

        // ------------------------------------------------------------------
        // After all files are read
        // ------------------------------------------------------------------
        /// <summary>Old dialogue.txt files had no [prompt] blocks, just the [rumors] and [smalltalk] pools. Keep those working.</summary>
        public void AddLegacyPrompts()
        {
            if (Pools.ContainsKey("rumors")) Prompts.Add(MakeLegacy("legacy.rumors", "Heard anything new?", "rumors"));
            if (Pools.ContainsKey("smalltalk")) Prompts.Add(MakeLegacy("legacy.smalltalk", "How are things out here?", "smalltalk"));
        }

        static PromptDef MakeLegacy(string id, string text, string pool)
        {
            var p = new PromptDef { Id = id, Text = text, Source = "built-in" };
            p.Replies.Add("pool:" + pool);
            return p;
        }

        /// <summary>Give every prompt an id; a later prompt with the same id replaces the earlier one.</summary>
        public void Finish()
        {
            var final = new List<PromptDef>();
            PromptsById.Clear();
            foreach (var p in Prompts)
            {
                if (p.Id.Length == 0) p.Id = "~" + (++autoCounter);

                PromptDef old;
                if (PromptsById.TryGetValue(p.Id, out old))
                {
                    Plugin.Log.LogInfo("Dialogue: prompt '" + p.Id + "' (" + p.Source + ") replaces the earlier one (" + old.Source + ").");
                    final[final.IndexOf(old)] = p;
                }
                else final.Add(p);

                PromptsById[p.Id] = p;
            }
            Prompts.Clear();
            Prompts.AddRange(final);
        }

        /// <summary>Report mistakes in the log so dialogue writers can find them.</summary>
        public void Validate()
        {
            foreach (var p in Prompts)
            {
                string loc = p.Source + " (prompt '" + p.Id + "')";
                if (p.Text.Length == 0)
                    Warn(loc, "has no 'text', so it can never be shown as a choice.");
                if (p.Replies.Count == 0 && p.Set.Count == 0 && p.Actions.Count == 0 && p.Next.Count == 0)
                    Warn(loc, "has no reply, set, action or next, so choosing it does nothing.");
                foreach (string r in p.Replies)
                {
                    if (!r.StartsWith("pool:", StringComparison.OrdinalIgnoreCase)) continue;
                    string pn = r.Substring(5).Trim();
                    if (!Pools.ContainsKey(pn)) Warn(loc, "reply refers to unknown pool '" + pn + "'.");
                }
                foreach (string id in p.Next)
                    if (!PromptsById.ContainsKey(id)) Warn(loc, "next refers to unknown prompt id '" + id + "'.");
            }
            foreach (var d in Intros)
                if (d.Lines.Count == 0) Warn(d.Source + " ([intro])", "has no 'line', so it is never used.");
        }

        // ------------------------------------------------------------------
        // Picking lines
        // ------------------------------------------------------------------
        /// <summary>Shuffled bag: every line is used once before any repeats.</summary>
        string Bag(string key, List<string> src)
        {
            Queue<string> q;
            if (!bags.TryGetValue(key, out q) || q.Count == 0)
            {
                q = new Queue<string>(src.OrderBy(_ => rng.Next()));
                bags[key] = q;
            }
            return q.Dequeue();
        }

        public string PickFromPool(string name)
        {
            List<string> src;
            if (!Pools.TryGetValue(name, out src) || src.Count == 0) return null;
            return Bag("pool:" + name, src);
        }

        /// <summary>Same NPC always gets the same pick (used for the opening line).</summary>
        public string PickSeeded(string poolName, System.Random r)
        {
            List<string> src;
            if (!Pools.TryGetValue(poolName, out src) || src.Count == 0) return null;
            return src[r.Next(src.Count)];
        }

        public string PickReply(PromptDef p)
        {
            if (p.Replies.Count == 0) return null;

            string entry;
            if (p.Sequence)
            {
                entry = p.Replies[Math.Min(p.SeqIndex, p.Replies.Count - 1)];
                p.SeqIndex++;
            }
            else if (p.Replies.Count == 1) entry = p.Replies[0];
            else entry = Bag("prompt:" + p.Id, p.Replies);

            if (entry.StartsWith("pool:", StringComparison.OrdinalIgnoreCase))
                return PickFromPool(entry.Substring(5).Trim()) ?? "...";
            return entry;
        }

        /// <summary>Best matching [intro]: highest priority wins, ties are chosen at random (seeded per NPC).</summary>
        public IntroDef FindIntro(string[] npcNames, System.Random r)
        {
            List<IntroDef> best = null;
            int bestPriority = int.MinValue;

            foreach (var it in Intros)
            {
                if (it.Lines.Count == 0) continue;
                if (!Rules.NpcMatches(it.NpcInclude, it.NpcExclude, npcNames)) continue;
                if (!Rules.Pass(it.Conditions)) continue;

                if (it.Priority > bestPriority)
                {
                    bestPriority = it.Priority;
                    best = new List<IntroDef>();
                }
                if (it.Priority == bestPriority) best.Add(it);
            }
            if (best == null) return null;
            return best[r.Next(best.Count)];
        }
    }
}
