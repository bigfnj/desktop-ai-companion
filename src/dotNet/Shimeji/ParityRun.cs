using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The converter's map from a converted pet back to the source pack (tools/ShimejiParity/CONTRACT.md v1.1 section 9):
    /// frame indexes to source image keys, nodes and child references to refKeys, and every emitted pose to the source
    /// Pose it came from. A test artefact: the product never reads one. Schema <c>shimeji-parity-map/2</c>:
    /// <code>{ "schema", "imageSet", "frames": [{ "frame", "image", "anchor", "size" }], "actions": { "&lt;node id&gt;": refKey },
    /// "children": { "&lt;node id&gt;:&lt;index&gt;": refKey }, "behaviours": { "&lt;node id&gt;": refKey },
    /// "sourceNames": { "&lt;node id&gt;": name or null }, "poses": { "&lt;node&gt;:&lt;variant&gt;:&lt;pose&gt;": { "refKey", "index" } } }</code>
    /// Map/1 had no poses, so the trace's pose.index was the position in the EMITTED variant, which a Duration-0 source
    /// pose the converter leaves out shifts for every later pose (N-shimeji-1106-13). A map/1 file is refused with a
    /// regenerate message, never read: there is no source index in it to fall back to.
    /// </summary>
    internal sealed class ParityMap
    {
        public const string Schema = "shimeji-parity-map/2";
        private const string RetiredSchema = "shimeji-parity-map/1";
        public string ImageSet;
        public readonly Dictionary<int, string> FrameImages = new Dictionary<int, string>();
        public readonly Dictionary<int, string> Actions = new Dictionary<int, string>();
        public readonly Dictionary<string, string> Children = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<int, string> Behaviours = new Dictionary<int, string>();
        /// <summary>CONTRACT v1.1 section 9 (decision 67, optional <c>"sourceNames": { "&lt;node id&gt;": name or null }</c>):
        /// the source's name of each behaviour or action node whose emitted name differs from it. A null value is a source
        /// element with no Name, "" an empty one; a node with no entry kept its name. Missing is never read as null.</summary>
        public readonly Dictionary<int, string> SourceNames = new Dictionary<int, string>();

        /// <summary>The map's poses as written: <c>node:variant:pose</c> to the source Pose's refKey and zero-based index.</summary>
        public readonly Dictionary<string, KeyValuePair<string, int>> Poses = new Dictionary<string, KeyValuePair<string, int>>(StringComparer.Ordinal);

        /// <summary>Each emitted pose's source index, bound by <see cref="Apply"/> to the compiled pose OBJECT. Identity,
        /// not equality, and kept here rather than on <see cref="TickPoseDef"/>, so the engine's model carries nothing a
        /// parity run needs: the trace looks up the pose a companion last applied, whichever action applied it, and two
        /// poses with equal fields in different variants are still two source poses.</summary>
        private readonly Dictionary<TickPoseDef, int> _poseIndex = new Dictionary<TickPoseDef, int>(ReferenceEqualityComparer.Instance);

        /// <summary>Parse and check a map/2 document. Every refusal is an <see cref="InvalidDataException"/> naming what is
        /// wrong; the caller adds the file. A key is canonical decimal (no sign, space or leading zero, within Int32), as the
        /// converter writes it, because int.Parse accepts "+2", " 2" and "02", which would each alias a real key; and a
        /// repeated key is refused, because JsonDocument keeps every repeat and a dictionary filled from it keeps the last.</summary>
        public static ParityMap Parse(string json)
        {
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("parity map is not a JSON object");
                Entries(root, null, "map");
                string schema = Str(root, "schema");
                if (schema == RetiredSchema)
                    throw new InvalidDataException("the map is " + RetiredSchema + "; regenerate it with the current converter (map/2 required)");
                if (schema != Schema) throw new InvalidDataException("parity map schema is not " + Schema);
                var map = new ParityMap { ImageSet = Str(root, "imageSet") };
                if (string.IsNullOrEmpty(map.ImageSet)) throw new InvalidDataException("parity map names no imageSet");
                JsonElement e;
                if (root.TryGetProperty("frames", out e))
                    foreach (JsonElement f in e.EnumerateArray())
                    {
                        map.FrameImages[f.GetProperty("frame").GetInt32()] = Str(f, "image");
                        map.ReadGeometry(f);
                    }
                foreach (JsonProperty p in Entries(root, "actions", "actions"))
                    map.Actions[NodeKey("actions", p.Name)] = p.Value.GetString();
                foreach (JsonProperty p in Entries(root, "children", "children"))
                {
                    string[] parts = p.Name.Split(':');
                    int node, child;
                    if (parts.Length != 2 || CanonicalFault(parts[0], out node) != null || CanonicalFault(parts[1], out child) != null)
                        throw new InvalidDataException("parity map children key '" + p.Name + "' is not node:child in canonical decimal");
                    map.Children[p.Name] = p.Value.GetString();
                }
                foreach (JsonProperty p in Entries(root, "behaviours", "behaviours"))
                    map.Behaviours[NodeKey("behaviours", p.Name)] = p.Value.GetString();
                foreach (JsonProperty p in Entries(root, "sourceNames", "sourceNames"))
                {
                    if (p.Value.ValueKind != JsonValueKind.Null && p.Value.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException("parity map sourceNames entry " + p.Name + " is neither a name nor null");
                    map.SourceNames[NodeKey("sourceNames", p.Name)] = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString();
                }
                if (!root.TryGetProperty("poses", out e)) throw new InvalidDataException("parity map has no poses (map/2 requires the object)");
                if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException("parity map poses is not an object");
                foreach (JsonProperty p in Entries(root, "poses", "poses")) map.ReadPose(p);
                return map;
            }
        }

        /// <summary>One <c>poses</c> entry (CONTRACT v1.1 section 9): a canonical <c>node:variant:pose</c> key, and an
        /// object holding exactly refKey (a non-empty <c>imageSet|file|path</c> whose last step is <c>Name[k]</c>) and index
        /// (a canonical nonnegative integer token equal to k - 1). TryGetInt32 alone would take "-0"; the raw token is read.</summary>
        private void ReadPose(JsonProperty p)
        {
            string key = p.Name;
            string[] parts = key.Split(':');
            if (parts.Length != 3) throw new InvalidDataException("parity map poses key '" + key + "' is not node:variant:pose");
            foreach (string part in parts)
            {
                int ignored;
                string fault = CanonicalFault(part, out ignored);
                if (fault == OutOfRange) throw new InvalidDataException("parity map poses key '" + key + "' has a part outside Int32");
                if (fault != null) throw new InvalidDataException("parity map poses key '" + key + "' is not node:variant:pose in canonical decimal");
            }
            JsonElement v = p.Value;
            if (v.ValueKind != JsonValueKind.Object) throw new InvalidDataException("parity map poses entry '" + key + "' is not an object");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty f in v.EnumerateObject())
            {
                if (!names.Add(f.Name)) throw new InvalidDataException("parity map poses entry '" + key + "' repeats its property '" + f.Name + "'");
                if (f.Name != "refKey" && f.Name != "index")
                    throw new InvalidDataException("parity map poses entry '" + key + "' has the property '" + f.Name + "'; a pose mapping holds only refKey and index");
            }
            if (names.Count != 2) throw new InvalidDataException("parity map poses entry '" + key + "' lacks refKey or index");
            JsonElement r = v.GetProperty("refKey"), i = v.GetProperty("index");
            if (r.ValueKind != JsonValueKind.String || r.GetString().Length == 0)
                throw new InvalidDataException("parity map poses entry '" + key + "' has a refKey that is not a non-empty string");
            int index;
            if (i.ValueKind != JsonValueKind.Number || CanonicalFault(i.GetRawText(), out index) != null)
                throw new InvalidDataException("parity map poses entry '" + key + "' has index " + i.GetRawText() + ", not a canonical nonnegative Int32");
            string refKey = r.GetString();
            string[] refParts = refKey.Split('|');
            if (refParts.Length != 3)
                throw new InvalidDataException("parity map poses entry '" + key + "' has a refKey of " + refParts.Length + " parts, not imageSet|file|path");
            if (refParts[0] != ImageSet)
                throw new InvalidDataException("parity map poses entry '" + key + "' has a refKey in image set '" + refParts[0] + "', not '" + ImageSet + "'");
            string step = refParts[2].Substring(refParts[2].LastIndexOf('/') + 1);
            int open = step.IndexOf('['), k;
            if (open <= 0 || step[step.Length - 1] != ']' || CanonicalFault(step.Substring(open + 1, step.Length - open - 2), out k) != null || k < 1)
                throw new InvalidDataException("parity map poses entry '" + key + "' has a refKey whose last step '" + step + "' is not Name[k]");
            if (index != k - 1)
                throw new InvalidDataException("parity map poses entry '" + key + "' has index " + index + " where its refKey's step " + step + " gives " + (k - 1));
            Poses.Add(key, new KeyValuePair<string, int>(refKey, index));
        }

        private const string OutOfRange = "outside Int32";

        /// <summary>Null for a canonical nonnegative decimal within Int32 (ASCII digits, no sign or space, no leading zero
        /// but "0" itself), else why not.</summary>
        internal static string CanonicalFault(string s, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s) || (s.Length > 1 && s[0] == '0')) return "not canonical";
            long v = 0;
            foreach (char c in s)
            {
                if (c < '0' || c > '9') return "not canonical";
                if (v <= int.MaxValue) v = v * 10 + (c - '0');
            }
            if (v > int.MaxValue) return OutOfRange;
            value = (int)v;
            return null;
        }

        private static int NodeKey(string section, string key)
        {
            int node;
            if (CanonicalFault(key, out node) != null)
                throw new InvalidDataException("parity map " + section + " key '" + key + "' is not a node id in canonical decimal");
            return node;
        }

        /// <summary>The properties of <paramref name="section"/> (the root itself when null), refusing a repeated key.</summary>
        private static List<JsonProperty> Entries(JsonElement root, string section, string label)
        {
            var list = new List<JsonProperty>();
            JsonElement e = root;
            if (section != null && !root.TryGetProperty(section, out e)) return list;
            if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException("parity map " + label + " is not an object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in e.EnumerateObject())
            {
                if (!seen.Add(p.Name)) throw new InvalidDataException("parity map repeats the " + label + " key '" + p.Name + "'");
                list.Add(p);
            }
            return list;
        }

        /// <summary>Give every node and child reference its refKey and every emitted pose its source index; a node the map
        /// does not name is a hole in the converter's map, refused rather than traced with a made-up key. Every node is
        /// walked by id: <see cref="TickType.Actions"/> is keyed by name, so a node another of the same name shadows is
        /// not in it, and its poses would go unchecked.</summary>
        public void Apply(TickType type)
        {
            string key;
            foreach (TickActDef act in type.ActionsById.Values)
            {
                if (!Actions.TryGetValue(act.NodeId, out key))
                    throw new InvalidDataException("parity map has no refKey for action node " + act.NodeId + " '" + act.Name + "'");
                act.RefKey = key;
                for (int i = 0; i < act.Children.Length; i++)
                    if (Children.TryGetValue(act.NodeId + ":" + i, out key)) act.Children[i].RefKey = key;
            }
            foreach (TickBehaviourDef b in type.BehavioursById.Values)
            {
                if (!Behaviours.TryGetValue(b.NodeId, out key))
                    throw new InvalidDataException("parity map has no refKey for behaviour node " + b.NodeId + " '" + b.Name + "'");
                b.RefKey = key;
            }
            // A source name for a node this type does not have is a converter fault, refused like a missing refKey.
            foreach (int node in SourceNames.Keys)
                if (!type.ActionsById.ContainsKey(node) && !type.BehavioursById.ContainsKey(node))
                    throw new InvalidDataException("parity map has a source name for node " + node + ", which is no action or behaviour of " + type.Name);
            BindPoses(type);
            CheckGeometry(type);
        }

        /// <summary>
        /// CONTRACT v1.1 section 9: every emitted pose has exactly one mapping, bound here by the pose object's identity,
        /// including image-less poses, false-condition variants and variants of instant actions the host never plays. The
        /// one exemption is decision 68's placeholder (<see cref="IsPlaceholder"/>), which the converter writes no mapping
        /// for; a mapping supplied for that shape is bound like any other, since an authored variant can have it. A key no
        /// emitted pose consumes (an unknown node, a composite node, a variant or pose out of range) is surplus, refused.
        /// </summary>
        private void BindPoses(TickType type)
        {
            var consumed = new HashSet<string>(StringComparer.Ordinal);
            foreach (TickActDef act in type.ActionsById.Values)
                for (int vi = 0; vi < act.Variants.Length; vi++)
                {
                    TickVariantDef variant = act.Variants[vi];
                    for (int pi = 0; pi < variant.Poses.Length; pi++)
                    {
                        string key = act.NodeId.ToString(CultureInfo.InvariantCulture) + ":" + vi.ToString(CultureInfo.InvariantCulture)
                            + ":" + pi.ToString(CultureInfo.InvariantCulture);
                        KeyValuePair<string, int> source;
                        if (Poses.TryGetValue(key, out source))
                        {
                            _poseIndex[variant.Poses[pi]] = source.Value;
                            consumed.Add(key);
                        }
                        else if (!IsPlaceholder(act, variant, variant.Poses[pi]))
                            throw new InvalidDataException("parity map has no source pose for " + key + " (action node " + act.NodeId + " '" + act.Name + "')");
                    }
                }
            foreach (string key in Poses.Keys)
                if (!consumed.Contains(key))
                    throw new InvalidDataException("parity map poses key '" + key + "' names no emitted pose of " + type.Name + " (surplus)");
        }

        /// <summary>
        /// Decision 68's placeholder, exactly: the sole variant of an Animate node or an Embedded Interact node, whose
        /// emitted when is the literal text <c>false</c>, holding one pose of frame -1, 1 tick, no move. The converter writes
        /// it for an action that ends before its first step and maps no source pose to it. A false condition alone is not
        /// enough: TickAnimate and TickInteract never apply a variant whose when is false (TickRuntime.cs RunAnimate,
        /// TickSocialFamily.cs TickInteract), but MoveWithTurn applies its LAST variant while turning without asking its
        /// condition (TickMotionFamily.cs), so an unmapped false variant there could be shown with no source pose. The
        /// test reads the attribute text (ScriptProgram.Source), never its value: "1 == 0" is false too, and is authored.
        /// </summary>
        internal static bool IsPlaceholder(TickActDef act, TickVariantDef variant, TickPoseDef pose)
        {
            return (act.Type == "Animate" || (act.Type == "Embedded" && act.Class == "Interact"))
                && act.Variants.Length == 1 && variant.When != null && variant.When.Source == "false"
                && variant.Poses.Length == 1 && pose.Frame == -1 && pose.Ticks == 1 && pose.Dx == 0 && pose.Dy == 0;
        }

        /// <summary>The source index bound to an applied pose, or false when this map binds no such pose object.</summary>
        public bool TryGetPoseIndex(TickPoseDef pose, out int index)
        {
            index = -1;
            return pose != null && _poseIndex.TryGetValue(pose, out index);
        }

        /// <summary>Each frame's authored anchor and size, when the map gives them (CONTRACT section 9).</summary>
        public readonly Dictionary<int, TickPoint> FrameAnchors = new Dictionary<int, TickPoint>();
        public readonly Dictionary<int, TickPoint> FrameSizes = new Dictionary<int, TickPoint>();

        private void ReadGeometry(JsonElement f)
        {
            int frame = f.GetProperty("frame").GetInt32();
            JsonElement v;
            if (f.TryGetProperty("anchor", out v)) FrameAnchors[frame] = new TickPoint(v[0].GetInt32(), v[1].GetInt32());
            if (f.TryGetProperty("size", out v)) FrameSizes[frame] = new TickPoint(v[0].GetInt32(), v[1].GetInt32());
        }

        /// <summary>The converter writes an image's anchor and size twice, on each pose that shows it (grimoire/06 section
        /// 3) and on its frame here; the trace's poseAnchor and origin come from the pose, so a pose that disagrees with
        /// its frame is a converter fault, refused before anything is traced. Unscaled: parity runs at scale 1. A right
        /// frame carries the pose's anchor too (the source anchors both images of a pair at one ImageAnchor).</summary>
        private void CheckGeometry(TickType type)
        {
            foreach (TickActDef act in type.ActionsById.Values)
                foreach (TickVariantDef v in act.Variants)
                    foreach (TickPoseDef p in v.Poses)
                    {
                        CheckFrame(act, p.Frame, p.AnchorX, p.AnchorY, p.Width, p.Height);
                        if (p.Right >= 0) CheckFrame(act, p.Right, p.AnchorX, p.AnchorY, p.RightWidth, p.RightHeight);
                    }
        }

        private void CheckFrame(TickActDef act, int frame, int anchorX, int anchorY, int width, int height)
        {
            TickPoint a, s;
            if (frame >= 0 && FrameAnchors.TryGetValue(frame, out a) && (a.X != anchorX || a.Y != anchorY))
                throw new InvalidDataException("parity map frame " + frame + " is anchored at " + a + " but action node "
                    + act.NodeId + " '" + act.Name + "' anchors it at (" + anchorX + ", " + anchorY + ")");
            if (frame >= 0 && FrameSizes.TryGetValue(frame, out s) && (s.X != width || s.Y != height))
                throw new InvalidDataException("parity map frame " + frame + " is " + s.X + "x" + s.Y + " but action node "
                    + act.NodeId + " '" + act.Name + "' shows it at " + width + "x" + height);
        }

        public string ImageOf(int frame)
        {
            string key;
            return frame >= 0 && FrameImages.TryGetValue(frame, out key) ? key : null;
        }

        private static string Str(JsonElement e, string name)
        {
            JsonElement v;
            return e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
    }

    /// <summary>The scenario's desktop (CONTRACT section 1), refreshed per tick the way the source's environment is:
    /// every area Set every tick (so its deltas are the change since the last tick), the cursor smoothed by
    /// Location.set, timeline entries taking effect at the start of their tick and holding until the next.</summary>
    internal sealed class ParityEnvironment : ITickEnvironment
    {
        private readonly int[] _screen;
        private readonly int[] _workArea;
        private readonly List<int[]> _screens = new List<int[]>();
        private readonly List<KeyValuePair<int, JsonElement>> _window = new List<KeyValuePair<int, JsonElement>>();
        private readonly List<KeyValuePair<int, JsonElement>> _input = new List<KeyValuePair<int, JsonElement>>();
        private readonly TickArea _screenArea = new TickArea();
        private readonly TickArea _workAreaArea = new TickArea();
        private readonly List<TickArea> _screenAreas = new List<TickArea>();
        private readonly TickArea _activeWindow = new TickArea { Visible = false };

        // The scenario's window as the desktop has it: set by a timeline entry at its tick, then moved or minimised by
        // the engine until the next entry replaces it (TickDesktopWindow models the source's WindowsEnvironment).
        private readonly TickDesktopWindow _desktopWindow = new TickDesktopWindow();
        private int _windowEntry = -1;
        private int _cursorX, _cursorY, _cursorDx, _cursorDy;
        private TickPoint _target;
        private readonly List<TickInput> _inputs = new List<TickInput>();
        private string _held;   // the companion holding a left press, which receives the pointer's moves

        public IReadOnlyList<TickInput> Inputs { get { return _inputs; } }

        public ParityEnvironment(JsonElement scenario)
        {
            foreach (JsonElement s in scenario.GetProperty("screens").EnumerateArray())
            {
                _screens.Add(Rect(s));
                _screenAreas.Add(new TickArea());
            }
            _workArea = Rect(scenario.GetProperty("workArea"));
            // Environment.getScreenRect: the bounding rectangle of every screen.
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
            foreach (int[] s in _screens)
            {
                l = Math.Min(l, s[0]); t = Math.Min(t, s[1]); r = Math.Max(r, s[0] + s[2]); b = Math.Max(b, s[1] + s[3]);
            }
            _screen = new[] { l, t, r - l, b - t };
            JsonElement e;
            if (scenario.TryGetProperty("activeWindow", out e))
                foreach (JsonElement w in e.EnumerateArray()) _window.Add(new KeyValuePair<int, JsonElement>(w.GetProperty("tick").GetInt32(), w.Clone()));
            if (scenario.TryGetProperty("input", out e))
                foreach (JsonElement i in e.EnumerateArray()) _input.Add(new KeyValuePair<int, JsonElement>(i.GetProperty("tick").GetInt32(), i.Clone()));
        }

        public TickArea Screen { get { return _screenArea; } }
        public TickArea WorkArea { get { return _workAreaArea; } }
        public IReadOnlyList<TickArea> Screens { get { return _screenAreas; } }
        public TickArea ActiveWindow { get { return _activeWindow; } }
        public TickPoint Cursor { get { return new TickPoint(_cursorX, _cursorY); } }
        public TickPoint CursorDelta { get { return new TickPoint(_cursorDx, _cursorDy); } }

        public void Tick(int tick)
        {
            _screenArea.Set(_screen[0], _screen[1], _screen[2], _screen[3]);
            for (int i = 0; i < _screens.Count; i++) _screenAreas[i].Set(_screens[i][0], _screens[i][1], _screens[i][2], _screens[i][3]);
            foreach (KeyValuePair<int, JsonElement> entry in _input)
            {
                JsonElement c;
                if (entry.Key == tick && entry.Value.TryGetProperty("cursor", out c))
                    _target = new TickPoint(c[0].GetInt32(), c[1].GetInt32());
            }
            // Location.set, integer division truncating toward zero as Java's.
            bool moved = _target.X != _cursorX || _target.Y != _cursorY;
            _cursorDx = (_cursorDx + _target.X - _cursorX) / 2;
            _cursorDy = (_cursorDy + _target.Y - _cursorY) / 2;
            _cursorX = _target.X;
            _cursorY = _target.Y;

            // Button edges for named companions (CONTRACT section 1). The pointer's move reaches the held companion
            // first, then this tick's edges in scenario order; a click is a press then a release.
            _inputs.Clear();
            var at = new TickPoint(_cursorX, _cursorY);
            if (moved && _held != null) _inputs.Add(new TickInput(TickInputKind.Move, _held, at));
            foreach (KeyValuePair<int, JsonElement> entry in _input)
            {
                if (entry.Key != tick) continue;
                JsonElement sidEl, b;
                string sid = entry.Value.TryGetProperty("sid", out sidEl) ? sidEl.GetString() : null;
                if (sid == null) continue;
                bool press = entry.Value.TryGetProperty("press", out b) && b.GetString() == "left";
                bool release = entry.Value.TryGetProperty("release", out b) && b.GetString() == "left";
                if (entry.Value.TryGetProperty("click", out b) && b.GetString() == "left") press = release = true;
                if (press) { _inputs.Add(new TickInput(TickInputKind.Press, sid, at)); _held = sid; }
                if (release) { _inputs.Add(new TickInput(TickInputKind.Release, sid, at)); _held = null; }
            }
            _workAreaArea.Set(_workArea[0], _workArea[1], _workArea[2], _workArea[3]);
            // The entry in force is the last one at or before this tick. Only a NEW entry resets the desktop's window, so
            // a move or minimise the engine made holds until the next entry.
            int latest = -1;
            for (int i = 0; i < _window.Count; i++)
                if (_window[i].Key <= tick) latest = i;
            if (latest != _windowEntry)
            {
                _windowEntry = latest;
                JsonElement window = _window[latest].Value;
                JsonElement visible, rect;
                bool shown = window.TryGetProperty("visible", out visible) && visible.ValueKind == JsonValueKind.True
                    && window.TryGetProperty("rect", out rect);
                if (shown)
                {
                    rect = window.GetProperty("rect");
                    int left = rect[0].GetInt32(), top = rect[1].GetInt32();
                    _desktopWindow.Show(new TickRect(left, top, rect[2].GetInt32() - left, rect[3].GetInt32() - top));
                }
                else _desktopWindow.Hide();
            }
            _desktopWindow.ApplyTo(_activeWindow, _screenArea);
        }

        public bool MoveActiveWindow(TickPoint topLeft, out TickRect from, out TickRect to)
        {
            return _desktopWindow.Move(topLeft, _activeWindow.Width, _activeWindow.Height, _screenArea, out from, out to);
        }

        public bool MinimizeActiveWindow()
        {
            return _desktopWindow.Minimize(_screenArea);
        }

        private static int[] Rect(JsonElement e)
        {
            return new[] { e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32(), e.GetProperty("w").GetInt32(), e.GetProperty("h").GetInt32() };
        }
    }

    /// <summary>CONTRACT section 2 with v1.2's 10.4: none (a draw yields 0.5; the comparer fails it as
    /// rng-unexpected-draw), a checked tape (a draw past its end yields 0.5 and the run goes on), or splitmix64.</summary>
    internal sealed class ParityRandom : ITickRandom
    {
        private readonly string _kind;
        private readonly List<double> _tape = new List<double>();
        private ulong _state;
        private int _next;
        private int _drawn;

        public ParityRandom(JsonElement rng)
        {
            _kind = rng.GetProperty("kind").GetString();
            JsonElement e;
            if (_kind == "tape" && rng.TryGetProperty("values", out e))
                foreach (JsonElement v in e.EnumerateArray()) _tape.Add(v.GetDouble());
            if (_kind == "splitmix64" && rng.TryGetProperty("seed", out e))
            {
                string seed = e.GetString();
                _state = ulong.Parse(seed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? seed.Substring(2) : seed, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>CONTRACT 10.5.1: whether this run actually drew more values than its tape holds; false for none and
        /// splitmix64. It is the footer's tapeExhausted on every trace/2 run, trace-off included. Exhaustion is behaviour
        /// the comparer grades from the draw records (rng-tape-exhausted), never a run failure, so nothing here ends the
        /// run or sets its status. Trace/1 ended the run in error instead, and the comparer, which grades a run failure
        /// before any RNG class, could then never report the exhaustion for the host (N-parity-1107-04).</summary>
        public bool TapeExhausted { get { return _kind == "tape" && _drawn > _tape.Count; } }

        /// <summary>How many values the tape holds; 0 for none and splitmix64, which have none.</summary>
        public int TapeLength { get { return _tape.Count; } }

        public double Next()
        {
            _drawn++;
            switch (_kind)
            {
                case "tape":
                    // 10.4: a draw past the end yields exactly 0.5, and the rng-draw event records that value.
                    if (_next >= _tape.Count) return 0.5;
                    return _tape[_next++];
                case "splitmix64":
                    unchecked
                    {
                        ulong z = _state += 0x9E3779B97F4A7C15UL;
                        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                        z ^= z >> 31;
                        return (z >> 11) * (1.0 / (1UL << 53));
                    }
                default:
                    // A directed scenario: the draw is logged by the world and the comparer fails it; 0.5 keeps the run
                    // going so the trace shows what drew.
                    return 0.5;
            }
        }
    }

    /// <summary>The scenario's settings, with typeSettings overriding per image set (CONTRACT section 1).</summary>
    internal sealed class ParitySettings : ITickSettings
    {
        private readonly Dictionary<string, bool> _global = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, bool>> _perType = new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);

        public ParitySettings(JsonElement scenario)
        {
            JsonElement e;
            if (scenario.TryGetProperty("settings", out e)) Read(e, _global);
            if (scenario.TryGetProperty("typeSettings", out e))
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    var d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    Read(p.Value, d);
                    _perType[p.Name] = d;
                }
        }

        public bool Get(string setting, string imageSet)
        {
            // The source's defaults: everything on except Multiscreen, which a scenario always states.
            return Stated(setting, imageSet) ?? !string.Equals(setting, "Multiscreen", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The scenario's value for a setting, per type first, then global; null when it does not say.</summary>
        internal bool? Stated(string setting, string imageSet)
        {
            Dictionary<string, bool> d;
            bool v;
            if (imageSet != null && _perType.TryGetValue(imageSet, out d) && d.TryGetValue(setting, out v)) return v;
            if (_global.TryGetValue(setting, out v)) return v;
            return null;
        }

        /// <summary>A toggleable behaviour: the scenario's value, else the toggle's header default, as the host's settings
        /// read it (TickTypeSwitches.IsBehaviourEnabled). A converted pack's toggles carry no default and are on; a
        /// hand-written one may default off, and the two runtimes must then agree.</summary>
        public bool IsBehaviourEnabled(TickBehaviourDef behaviour, string imageSet)
        {
            if (string.IsNullOrEmpty(behaviour.Toggle)) return true;
            return Stated("behaviour:" + behaviour.Toggle, imageSet) ?? behaviour.ToggleDefault;
        }

        private static void Read(JsonElement e, Dictionary<string, bool> into)
        {
            foreach (JsonProperty p in e.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.True || p.Value.ValueKind == JsonValueKind.False)
                    into[p.Name] = p.Value.ValueKind == JsonValueKind.True;
        }
    }

    /// <summary>
    /// The host's trace writer (CONTRACT section 3). It is an observer: every value it writes was computed by the
    /// engine at the source's own evaluation sites, or is stored state, so tracing changes nothing.
    /// </summary>
    internal sealed class ParityTraceWriter : ITickObserver
    {
        private readonly TextWriter _trace;
        private readonly EventSink _out;
        private readonly bool _traceOff;
        private readonly Dictionary<string, ParityMap> _maps;
        private int _tick;
        private int _states, _events, _draws;
        private readonly Dictionary<string, TickPoint> _lastAnchor = new Dictionary<string, TickPoint>(StringComparer.Ordinal);
        private readonly Dictionary<string, TickPoint> _velocity = new Dictionary<string, TickPoint>(StringComparer.Ordinal);

        /// <param name="traceOff">CONTRACT v1.1 section 5's trace-off run: every event record is formed and folded into
        /// <see cref="EventSequenceSha256"/> exactly as a traced run forms it, but no event or per-tick state line is
        /// written; <see cref="FinalStates"/> writes the last tick's states once. Its pair with a traced run of the same
        /// scenario proves that observing changes nothing (ShimejiWindowSelfTest's trace-on/off proof).</param>
        public ParityTraceWriter(TextWriter output, Dictionary<string, ParityMap> maps, bool traceOff = false)
        {
            _trace = output;
            _traceOff = traceOff;
            _out = new EventSink(traceOff ? null : output);
            _maps = maps;
        }

        public int Tick { set { _tick = value; } }

        /// <summary>The draws and events formed so far, written or withheld.</summary>
        public int Draws { get { return _draws; } }
        public int Events { get { return _events; } }

        /// <summary>CONTRACT v1.1 section 5: SHA-256 over every event record's UTF-8 line and one LF, in output order.</summary>
        public string EventSequenceSha256 { get { return _out.Digest(); } }

        /// <summary>Where every event line goes: folded into the event digest, then written unless the run is trace-off.
        /// The digest is over the bytes a traced run writes (the writer's lines end in "\n", ParityRun sets NewLine).</summary>
        private sealed class EventSink
        {
            private readonly TextWriter _output;
            private readonly IncrementalHash _digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            public EventSink(TextWriter output) { _output = output; }

            public void WriteLine(string line)
            {
                _digest.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
                if (_output != null) _output.WriteLine(line);
            }

            /// <summary>The digest so far; GetCurrentHash leaves the running state as it was.</summary>
            public string Digest() { return Hex(_digest.GetCurrentHash()); }
        }

        private static string Hex(byte[] hash)
        {
            var b = new StringBuilder(64);
            foreach (byte x in hash) b.Append(x.ToString("x2", CultureInfo.InvariantCulture));
            return b.ToString();
        }

        public void Header(string scenarioId, string scenarioSha, string packSha, Dictionary<string, string> build, string rngJson, double dpiScale)
        {
            // CONTRACT 10.1 and 10.2: the schema is the trace's only version declaration, and no other header field changes
            // with it. Trace/2 is what the host writes: every footer carries tapeExhausted and a tape's end is not a run
            // failure (10.5).
            var w = new JsonLine();
            w.Str("type", "header").Str("schema", "shimeji-parity-trace/2").Str("runtime", "host");
            w.Raw("scenario", "{\"id\":" + JsonLine.Quote(scenarioId) + ",\"sha256\":" + JsonLine.Quote(scenarioSha)
                + ",\"packSha256\":" + JsonLine.Quote(packSha) + "}");
            var b = new StringBuilder("{");
            bool first = true;
            foreach (KeyValuePair<string, string> p in build)
            {
                if (!first) b.Append(',');
                first = false;
                b.Append(JsonLine.Quote(p.Key)).Append(':').Append(JsonLine.Quote(p.Value));
            }
            w.Raw("build", b.Append('}').ToString()).Raw("rng", rngJson).Num("tickMs", 40).Num("dpiScale", dpiScale)
                .Str("runId", Guid.NewGuid().ToString()).Str("startedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            _trace.WriteLine(w.End());
        }

        /// <summary>This tick's state lines. A trace-off run forms none: it keeps only each companion's committed anchor
        /// change, which is stored state, so its final lines carry the same velocity a traced run's would.</summary>
        public void States(TickWorld world)
        {
            if (_traceOff)
            {
                foreach (TickMascot m in world.Mascots) _velocity[m.Sid] = Velocity(m);
                foreach (TickMascot m in world.Pending) _velocity[m.Sid] = Velocity(m);
                return;
            }
            foreach (TickMascot m in world.Mascots) State(m, false, Velocity(m));
            foreach (TickMascot m in world.Pending) State(m, true, Velocity(m));
        }

        /// <summary>A trace-off run's one set of state lines, the last tick's, written at its end; nothing for a traced run,
        /// whose last tick's lines are already written.</summary>
        public void FinalStates(TickWorld world)
        {
            if (!_traceOff) return;
            foreach (TickMascot m in world.Mascots) State(m, false, _velocity[m.Sid]);
            foreach (TickMascot m in world.Pending) State(m, true, _velocity[m.Sid]);
        }

        private TickPoint Velocity(TickMascot m)
        {
            TickPoint prev;
            TickPoint velocity = _lastAnchor.TryGetValue(m.Sid, out prev) ? new TickPoint(m.Anchor.X - prev.X, m.Anchor.Y - prev.Y) : new TickPoint(0, 0);
            _lastAnchor[m.Sid] = m.Anchor;
            return velocity;
        }

        private void State(TickMascot m, bool pending, TickPoint velocity)
        {
            // The pose belongs to the member whose configuration defines it, the type that built the action applying it
            // (TickAction.OwnerType, which composites hand their children), which after a Transform is still the old one.
            TickType poseType = m.PoseType ?? m.Type;
            ParityMap map;
            _maps.TryGetValue(poseType.Name, out map);
            TickBehaviour beh = m.Behaviour;
            TickAction leaf = beh == null ? null : beh.Action.Leaf;
            List<string> stack = beh == null ? new List<string>() : beh.ActionStack();
            var w = new JsonLine();
            w.Str("type", "state").Num("tick", _tick).Str("sid", m.Sid).Str("imageSet", m.Type.Name)
                .Str("behavior", BehaviourName(beh)).Str("action", leaf == null ? null : NodeName(beh.Type, leaf.Def.NodeId, leaf.Name))
                .Str("actionClass", leaf == null ? null : (leaf.Def.Type == "Embedded" ? leaf.Def.Class : leaf.Def.Type))
                .StrList("actionStack", stack).Str("refKey", stack.Count > 0 ? stack[stack.Count - 1] : null)
                .Num("actionAge", leaf == null ? (double?)null : leaf.Time);
            if (m.Pose == null) w.Raw("pose", "null").Raw("poseAnchor", "null");
            else
            {
                // CONTRACT v1.1 section 9: index is the SOURCE pose's position, bound to the applied pose object by the
                // member's map. No map, or no binding, is a provenance fault that ends the run in error, with index null:
                // the position in the emitted variant is not the source's (a Duration-0 source pose is left out of it)
                // and is never written in its place.
                int index = -1;
                bool bound = map != null && map.TryGetPoseIndex(m.Pose, out index);
                if (!bound)
                {
                    if (ProvenanceFault == null)
                        ProvenanceFault = "tick " + _tick + " " + m.Sid + ": applied pose has no source binding (member " + poseType.Name
                            + (map == null ? ", which has no parity map)" : ")");
                }
                w.Raw("pose", "{\"image\":" + JsonLine.Quote(map == null ? null : map.ImageOf(m.Pose.Frame))
                    + ",\"imageRight\":" + JsonLine.Quote(map == null ? null : map.ImageOf(m.Pose.Right))
                    + ",\"index\":" + (bound ? index.ToString(CultureInfo.InvariantCulture) : "null") + "}");
                // CONTRACT section 3: the effective anchor while the applied pose has an image, null while it has none.
                TickPoint c = m.ImageCenter;
                w.Raw("poseAnchor", m.HasImage ? "[" + c.X + "," + c.Y + "]" : "null");
            }
            w.Bool("visible", m.Visible).Raw("anchor", "[" + m.Anchor.X + "," + m.Anchor.Y + "]");
            if (m.Pose == null) w.Raw("origin", "null");
            else
            {
                // Mascot.getBounds (Mascot.java:714-729): the anchor less the image's anchor with an image; without one
                // the window's last bounds, which do not follow the anchor while the companion is hidden.
                TickRect b = m.Bounds;
                w.Raw("origin", "[" + b.X + "," + b.Y + "]");
            }
            w.Bool("lookRight", m.LookRight).Raw("velocity", "[" + velocity.X + "," + velocity.Y + "]")
                .StrList("affordances", m.Affordances).Str("target", m.Target == null ? null : m.Target.Sid).Bool("dragging", m.Dragging)
                .Bool("pending", pending).Bool("disposing", m.Disposing).Raw("vars", Vars(m));
            _trace.WriteLine(w.End());
            _states++;
        }

        private static string Vars(TickMascot m)
        {
            var b = new StringBuilder("{");
            bool first = true;
            var keys = new List<string>(m.Variables.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string k in keys)
            {
                ScriptValue v = m.Variables[k];
                if (v.Kind == ScriptValueKind.Point) continue;   // stored SCALAR variables only
                if (!first) b.Append(',');
                first = false;
                b.Append(JsonLine.Quote(k)).Append(':').Append(JsonLine.Value(v));
            }
            return b.Append('}').ToString();
        }

        private JsonLine Event(TickMascot m, string kind)
        {
            _events++;
            return new JsonLine().Str("type", "event").Num("tick", _tick).Str("sid", m.Sid).Str("kind", kind);
        }

        // CONTRACT v1.1 section 9 (decision 67): a behaviour or action is written by the name its source node has, read
        // from the map of the member that node belongs to, and only where conversion renamed it; a node the map does
        // not list keeps its emitted name, which is then the source's. A null entry stays null (never the synthetic
        // name) and "" stays "". Every lookup reads stored state: nothing is evaluated and no event is added to learn a
        // name. A name whose node cannot be identified is not guessed: the run is marked failed (ProvenanceFault).
        private readonly Dictionary<TickMascot, TickBehaviour> _entered = new Dictionary<TickMascot, TickBehaviour>();

        /// <summary>The first name this writer could not tie to a node, or null; ParityRun ends such a run in error.</summary>
        public string ProvenanceFault { get; private set; }

        private string NodeName(TickType owner, int nodeId, string emitted)
        {
            ParityMap map;
            string name;
            if (owner != null && _maps.TryGetValue(owner.Name, out map) && map.SourceNames.TryGetValue(nodeId, out name)) return name;
            return emitted;
        }

        /// <summary>A behaviour's source name, from the configuration it was built from (after a Transform the old
        /// behaviour still belongs to the old member).</summary>
        private string BehaviourName(TickBehaviour behaviour)
        {
            return behaviour == null ? null : NodeName(behaviour.Type, behaviour.Def.NodeId, behaviour.Name);
        }

        /// <summary>A behaviour name REQUESTED of a member (a Transform's, a hotspot's): the root node it names there,
        /// whether or not the request then succeeds; a name that member has no root behaviour for was never renamed,
        /// so it stays as requested. Never the current or fallback behaviour's name.</summary>
        private string RequestedName(TickType member, string requested)
        {
            TickBehaviourDef def;
            if (requested == null || member == null || !member.RootByName.TryGetValue(requested, out def)) return requested;
            return NodeName(member, def.NodeId, requested);
        }

        private string Unattributed(TickMascot mascot, string field, string name)
        {
            if (ProvenanceFault == null)
                ProvenanceFault = "tick " + _tick + " " + mascot.Sid + ": the behaviour event's " + field + " '" + name + "' has no node to name it by";
            return name;
        }

        public void BehaviourEntered(TickMascot mascot, string from, string to, string cause)
        {
            // Mascot.setBehavior is the only place a behaviour changes (TickMascot.SetBehaviour), and it reports here
            // after the change: "to" is the companion's behaviour now, "from" the one this writer saw it enter last.
            TickBehaviour previous, current = mascot.Behaviour;
            _entered.TryGetValue(mascot, out previous);
            string fromName = from == null ? null
                : previous != null && previous.Name == from ? BehaviourName(previous) : Unattributed(mascot, "from", from);
            string toName = to == null ? null
                : current != null && current.Name == to ? BehaviourName(current) : Unattributed(mascot, "to", to);
            _entered[mascot] = current;
            _out.WriteLine(Event(mascot, "behavior").Str("from", fromName).Str("to", toName).Str("cause", cause).End());
        }

        public void BirthRequested(TickMascot parent, TickMascot child, int ordinal, int attempt)
        {
            _out.WriteLine(Event(parent, "birth-request").Str("child", child.Sid).Str("imageSet", child.Type.Name)
                .Str("behavior", BehaviourName(child.Behaviour))
                .Raw("anchor", "[" + child.Anchor.X + "," + child.Anchor.Y + "]").Bool("lookRight", child.LookRight)
                .Num("ordinal", ordinal).Num("attempt", attempt).End());
        }

        public void BirthRefused(TickMascot parent, int attempt, string reason)
        {
            _out.WriteLine(Event(parent, "birth-refused").Raw("child", "null").Num("attempt", attempt).Str("reason", reason).End());
        }

        public void BirthActivated(TickMascot child)
        {
            _out.WriteLine(Event(child, "birth-activate").Str("child", child.Sid).End());
        }

        public void Transformed(TickMascot mascot, string fromImageSet, string toImageSet, string behaviour)
        {
            // The requested TransformBehavior names a behaviour of the member transformed into, which is the companion's
            // type by now; a failed request (blank, unknown) keeps its requested name.
            _out.WriteLine(Event(mascot, "transform").Str("fromImageSet", fromImageSet).Str("toImageSet", toImageSet)
                .Str("behavior", RequestedName(mascot.Type, behaviour)).End());
        }

        public void DisposeRequested(TickMascot mascot, string reason)
        {
            _out.WriteLine(Event(mascot, "dispose-request").Str("reason", reason).End());
        }

        public void Disposed(TickMascot mascot)
        {
            _out.WriteLine(Event(mascot, "dispose").End());
        }

        public void RandomDrawn(TickMascot mascot, string site, double value, string purpose)
        {
            _draws++;
            _out.WriteLine(Event(mascot, "rng-draw").Str("site", site).Num("occurrence", _draws).Num("value", value).Str("purpose", purpose).End());
        }

        public void Fault(TickMascot mascot, string message)
        {
            _out.WriteLine(Event(mascot, "error").Str("message", message).End());
        }

        public void Muted(TickMascot mascot, string clip)
        {
            _out.WriteLine(Event(mascot, "mute").Str("clip", clip).End());
        }

        /// <summary>A clip the engine started, or a running one a Mute stopped (TickSound). A parity run has no clip output,
        /// so no clip is loaded there and none is written until the contract defines a clip clock.</summary>
        public void Sound(TickMascot mascot, string clip, string op)
        {
            _out.WriteLine(Event(mascot, "sound").Str("clip", clip).Str("op", op).End());
        }

        public void ScanTargeted(TickMascot mascot, string affordance, TickMascot target)
        {
            _out.WriteLine(Event(mascot, "scan-target").Str("affordance", affordance).Str("target", target == null ? null : target.Sid).End());
        }

        public void Interacted(TickMascot mascot, IReadOnlyList<TickMascot> with)
        {
            var sids = new List<string>();
            foreach (TickMascot m in with) sids.Add(m.Sid);
            _out.WriteLine(Event(mascot, "interact").StrList("with", sids).End());
        }

        public void WindowMoved(TickMascot mascot, TickRect from, TickRect to)
        {
            _out.WriteLine(Event(mascot, "window-move").Raw("from", Ltrb(from)).Raw("to", Ltrb(to)).End());
        }

        public void WindowMinimized(TickMascot mascot)
        {
            _out.WriteLine(Event(mascot, "window-minimize").End());
        }

        public void Dragged(TickMascot mascot, string op)
        {
            _out.WriteLine(Event(mascot, "drag").Str("op", op).End());
        }

        public void Hotspot(TickMascot mascot, string op, string behaviour)
        {
            // A hotspot's behaviour is looked up in the configuration of the behaviour that holds the hotspot.
            _out.WriteLine(Event(mascot, "hotspot").Str("op", op)
                .Str("behavior", RequestedName(mascot.Behaviour == null ? mascot.Type : mascot.Behaviour.Type, behaviour)).End());
        }

        /// <summary>A rectangle as the scenario writes a window's: [left, top, right, bottom].</summary>
        private static string Ltrb(TickRect r)
        {
            return "[" + r.X + "," + r.Y + "," + (r.X + r.Width) + "," + (r.Y + r.Height) + "]";
        }

        public void Footer(int ticks, string status, bool tapeExhausted)
        {
            // A trace-off footer counts the lines it holds (no draws or events) and carries what it withheld, as the
            // oracle's observation run does (CONTRACT v1.1 sections 3 and 5); an ordinary host footer needs none of it.
            // tapeExhausted is required on EVERY trace/2 footer, trace-off included (10.5.1, 10.8): the observation proof
            // compares the two runs' flags, and a trace-off run has no draw lines to read it from.
            JsonLine footer = new JsonLine().Str("type", "footer").Num("ticks", ticks).Str("status", status)
                .Num("draws", _traceOff ? 0 : _draws).Num("states", _states).Num("events", _traceOff ? 0 : _events)
                .Bool("tapeExhausted", tapeExhausted);
            if (_traceOff)
                footer.Num("unobservedDraws", _draws).Num("unobservedEvents", _events).Str("eventSequenceSha256", EventSequenceSha256);
            _trace.WriteLine(footer.End());
        }
    }

    /// <summary>A compact, ordered JSON object writer (key order is part of a readable trace; the comparer does not
    /// depend on it).</summary>
    internal sealed class JsonLine
    {
        private readonly StringBuilder _b = new StringBuilder("{");
        private bool _first = true;

        private JsonLine Key(string k)
        {
            if (!_first) _b.Append(',');
            _first = false;
            _b.Append(Quote(k)).Append(':');
            return this;
        }

        public JsonLine Str(string k, string v) { Key(k)._b.Append(Quote(v)); return this; }
        public JsonLine Raw(string k, string json) { Key(k)._b.Append(json); return this; }
        public JsonLine Bool(string k, bool v) { Key(k)._b.Append(v ? "true" : "false"); return this; }
        public JsonLine Num(string k, double? v) { Key(k)._b.Append(v == null ? "null" : Number(v.Value)); return this; }

        public JsonLine StrList(string k, IEnumerable<string> values)
        {
            var b = new StringBuilder("[");
            bool first = true;
            foreach (string v in values)
            {
                if (!first) b.Append(',');
                first = false;
                b.Append(Quote(v));
            }
            return Raw(k, b.Append(']').ToString());
        }

        public string End() { return _b.Append('}').ToString(); }

        public static string Number(double v)
        {
            if (v == Math.Floor(v) && Math.Abs(v) < 1e15) return ((long)v).ToString(CultureInfo.InvariantCulture);
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        public static string Value(ScriptValue v)
        {
            switch (v.Kind)
            {
                case ScriptValueKind.Null: return "null";
                case ScriptValueKind.Boolean: return v.Number != 0 ? "true" : "false";
                case ScriptValueKind.Number: return Number(v.Number);
                default: return Quote(v.ToDisplayString());
            }
        }

        public static string Quote(string s)
        {
            if (s == null) return "null";
            var b = new StringBuilder(s.Length + 2).Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else b.Append(c);
                        break;
                }
            }
            return b.Append('"').ToString();
        }
    }

    /// <summary>The command-line front of <see cref="ParityRun"/>: arguments, the test-root gate, the build witness.</summary>
    internal static class ParityRunCommand
    {
        public const string Flag = "--shimeji-parity-run";

        /// <summary>The assembly metadata key the project file stamps the full source revision under (its
        /// StampDesktopAICompanionSourceRevision target). Only this command reads it.</summary>
        internal const string SourceRevisionKey = "DesktopAICompanionSourceRevision";

        /// <summary><c>--shimeji-parity-run &lt;scenario.json&gt; &lt;pets dir&gt; &lt;trace out&gt; &lt;converter exe&gt;</c>.
        /// Exit 0 = a complete trace, 1 = the run ended in error (the trace says why), 2 = refused.</summary>
        public static int Execute(string[] args, int at, TextWriter log)
        {
            return Execute(args, at, log, typeof(ParityRun).Assembly);
        }

        /// <param name="stamped">The assembly whose stamped source revision is build.commit: this build's own, except in
        /// ShimejiWindowSelfTest, which hands it one with no stamp to see the run refused.</param>
        internal static int Execute(string[] args, int at, TextWriter log, System.Reflection.Assembly stamped)
        {
            if (args.Length < at + 5)
            {
                log.WriteLine("usage: " + Flag + " <scenario.json> <pets dir> <trace out> <converter exe>");
                return 2;
            }
            // The build's identity first, before the data-root gate and before anything is read or a trace opened. A
            // missing or malformed revision is refused rather than written as a placeholder, which the comparer's non-empty
            // check would pass while it names nothing (contract review item 13), and it is never replaced by the product
            // version (N-bd-parity-host-1-03).
            string refusal;
            string commit = SourceRevision(stamped, out refusal);
            if (commit == null)
            {
                log.WriteLine(Flag + " refused: " + refusal + ". A parity trace names the source revision its build came from;"
                    + " rebuild from a git checkout.");
                return 2;
            }
            if (!AppPaths.IsDataRootOverridden)
            {
                log.WriteLine(Flag + " runs only under an overridden test data root (" + AppPaths.DataRootOverrideEnvironmentVariable + ").");
                return 2;
            }
            var build = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "commit", commit },
                { "exeSha256", ParityRun.Sha256(File.ReadAllBytes(Environment.ProcessPath)) },
                { "engineSha256", ParityRun.Sha256(File.ReadAllBytes(typeof(TickWorld).Assembly.Location)) },
                { "converterSha256", ParityRun.ConverterSha256(args[at + 4]) },
            };
            return ParityRun.Run(args[at + 1], args[at + 2], args[at + 3], build, log);
        }

        /// <summary>
        /// build.commit: the full source revision the build stamped into its OWN metadata value, or null with the reason in
        /// <paramref name="refusal"/>. Never the informational version, which is the product version (DesktopAICompanionVersion;
        /// two builds of one version share it), and never a placeholder.
        /// </summary>
        internal static string SourceRevision(System.Reflection.Assembly stamped, out string refusal)
        {
            string value = null;
            foreach (System.Reflection.AssemblyMetadataAttribute a in stamped.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
                if (a.Key == SourceRevisionKey) value = a.Value;
            if (value == null)
            {
                refusal = "this build carries no source revision (no " + SourceRevisionKey + " metadata: it was built without git or outside a checkout)";
                return null;
            }
            if (!IsFullRevision(value))
            {
                refusal = "this build's source revision '" + value + "' is not a full commit id (40 lowercase hex characters, not all zero)";
                return null;
            }
            refusal = null;
            return value;
        }

        /// <summary>A full git commit id as rev-parse prints it: 40 lowercase hex characters, and not git's all-zero null
        /// id, which names no commit.</summary>
        internal static bool IsFullRevision(string value)
        {
            if (value == null || value.Length != 40 || value == new string('0', 40)) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }
    }

    /// <summary>
    /// <c>--shimeji-parity-run &lt;scenario.json&gt; &lt;pets dir&gt; &lt;trace out&gt;</c>: run one scenario headless, with
    /// no window, no real input and no real environment, and write the host's trace. The pets directory holds one
    /// folder per image set with its converted <c>animations.xml</c> and <c>parity-map.json</c>.
    /// </summary>
    internal static class ParityRun
    {
        /// <summary>A left-button edge whose companion is not active at dispatch (CONTRACT v1.1 section 1): the scenario
        /// is invalid, so the run stops there.</summary>
        internal sealed class ScenarioInvalidException : Exception
        {
            public ScenarioInvalidException(string message) : base(message) { }
        }

        /// <summary>What a map, document or compile refusal can throw: each is the pet's fault, reported with its file
        /// and exit 2, never an unhandled exception out of the command.</summary>
        private static bool IsRefusal(Exception e)
        {
            return e is InvalidDataException || e is JsonException || e is FormatException || e is InvalidOperationException
                || e is KeyNotFoundException || e is OverflowException;
        }

        /// <summary>
        /// Every converted pet in <paramref name="petsDir"/>: its document validated and compiled, its map parsed, checked
        /// and bound. Null, with the refusal logged, when any is refused. A folder with animations.xml and no
        /// parity-map.json (or the reverse) is refused, not skipped: a member a Breed or Transform names that is absent
        /// from the world falls back to the parent's type (TickPacks), inside a trace that would look valid.
        /// </summary>
        internal static List<KeyValuePair<TickType, ParityMap>> LoadPets(string petsDir, TextWriter log)
        {
            var loaded = new List<KeyValuePair<TickType, ParityMap>>();
            foreach (string dir in Directory.GetDirectories(petsDir))
            {
                string xmlPath = Path.Combine(dir, "animations.xml"), mapPath = Path.Combine(dir, "parity-map.json");
                bool hasXml = File.Exists(xmlPath), hasMap = File.Exists(mapPath);
                if (!hasXml && !hasMap) continue;
                if (!hasMap)
                {
                    log.WriteLine("refused " + dir + ": a converted pet with no parity-map.json beside its animations.xml");
                    return null;
                }
                if (!hasXml)
                {
                    log.WriteLine("refused " + dir + ": a parity-map.json with no animations.xml beside it");
                    return null;
                }
                XmlData.RootNode root;
                string error;
                if (!CompanionXmlValidator.TryParseWithFeatures(File.ReadAllText(xmlPath), CompanionFormatFeatures.All, out root, out error))
                {
                    log.WriteLine("refused " + xmlPath + ": " + error);
                    return null;
                }
                string at = mapPath;
                try
                {
                    ParityMap map = ParityMap.Parse(File.ReadAllText(mapPath));
                    at = xmlPath;
                    TickType type = TickType.Compile(map.ImageSet, root);
                    at = mapPath;
                    map.Apply(type);
                    loaded.Add(new KeyValuePair<TickType, ParityMap>(type, map));
                }
                catch (Exception e) when (IsRefusal(e))
                {
                    log.WriteLine("refused " + at + ": " + e.Message);
                    return null;
                }
            }
            return loaded;
        }

        /// <param name="traceOff">CONTRACT v1.1 section 5: form every record and write only the final states and a footer
        /// carrying the withheld totals and the event digest (<see cref="ParityTraceWriter"/>).</param>
        public static int Run(string scenarioPath, string petsDir, string tracePath, Dictionary<string, string> build, TextWriter log, bool traceOff = false)
        {
            byte[] scenarioBytes = File.ReadAllBytes(scenarioPath);
            using (JsonDocument doc = JsonDocument.Parse(scenarioBytes))
            {
                // Every map is parsed, compiled and applied before the trace file is opened, so a refused map leaves no
                // trace behind, not even an empty one a later step could take for a run.
                List<KeyValuePair<TickType, ParityMap>> pets = LoadPets(petsDir, log);
                if (pets == null) return 2;
                using (var trace = new StreamWriter(tracePath, false, new UTF8Encoding(false)))
                {
                    trace.NewLine = "\n";
                    return Run(doc.RootElement, scenarioPath, scenarioBytes, pets, trace, build, log, traceOff);
                }
            }
        }

        private static int Run(JsonElement s, string scenarioPath, byte[] scenarioBytes, List<KeyValuePair<TickType, ParityMap>> pets,
            TextWriter trace, Dictionary<string, string> build, TextWriter log, bool traceOff)
        {
            {
                var maps = new Dictionary<string, ParityMap>(StringComparer.Ordinal);
                var types = new List<TickType>();
                foreach (KeyValuePair<TickType, ParityMap> pet in pets)
                {
                    maps[pet.Key.Name] = pet.Value;
                    types.Add(pet.Key);
                }

                var env = new ParityEnvironment(s);
                var random = new ParityRandom(s.GetProperty("rng"));
                var settings = new ParitySettings(s);
                var writer = new ParityTraceWriter(trace, maps, traceOff);
                string packDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scenarioPath)), s.GetProperty("pack").GetString()));
                writer.Header(s.GetProperty("id").GetString(), Sha256(scenarioBytes), PackSha256(packDir), build,
                    // Re-serialised compactly: the scenario's own text may be indented, and a trace is one object per line.
                    JsonSerializer.Serialize(s.GetProperty("rng")), s.GetProperty("dpiScale").GetDouble());

                var world = new TickWorld(env, random, settings, writer);
                foreach (TickType t in types) world.AddType(t);
                int ticks = s.GetProperty("ticks").GetInt32();
                string status = "complete";
                // CONTRACT v1.1 section 1: a left-button edge for a companion that is not active at dispatch is a scenario
                // error (the oracle refuses it the same way). Only this run sets the hook: the product drops such an edge.
                world.InactiveButtonTarget = input =>
                {
                    throw new ScenarioInvalidException("scenario-invalid: tick " + world.Step + ": left-button target " + input.Sid + " is not active");
                };
                try
                {
                    writer.Tick = 0;
                    world.Start();
                    foreach (JsonElement m in s.GetProperty("mascots").EnumerateArray())
                    {
                        TickType type = world.FindType(m.GetProperty("imageSet").GetString());
                        if (type == null) throw new InvalidDataException("no converted pet for image set " + m.GetProperty("imageSet").GetString());
                        JsonElement a = m.GetProperty("anchor");
                        JsonElement beh;
                        world.CreateAtStart(m.GetProperty("sid").GetString(), type, new TickPoint(a[0].GetInt32(), a[1].GetInt32()),
                            m.GetProperty("lookRight").GetBoolean(), m.TryGetProperty("behavior", out beh) ? beh.GetString() : null);
                    }
                    // Every start companion is initialised; now they join the manager without a step (CONTRACT section 4).
                    world.ActivateStartup();
                    writer.States(world);
                    // A tape's end does not stop the loop (CONTRACT 10.5.1): a draw past it yields 0.5, the run steps on to
                    // tick N, and Finish logs the exhaustion and writes it as the footer's tapeExhausted.
                    for (int t = 1; t <= ticks; t++)
                    {
                        writer.Tick = t;
                        world.StepOnce();
                        writer.States(world);
                    }
                    writer.FinalStates(world);
                }
                catch (ScenarioInvalidException e)
                {
                    // Nothing more is dispatched and the tick writes no state lines; the footer says aborted, and the exit
                    // is a refusal's, since the scenario, not the runtime, is at fault.
                    log.WriteLine(e.Message);
                    Finish(writer, ticks, "aborted", random, log);
                    return 2;
                }
                catch (Exception e)
                {
                    status = "error";
                    log.WriteLine("run failed: " + e);
                }
                return Finish(writer, ticks, status, random, log);
            }
        }

        /// <summary>The end of every run: a name the writer could not tie to a node (its ProvenanceFault, decision 67)
        /// turns the status to error and is logged, then the footer is written and the exit code returned. Run's only
        /// ending; internal so ShimejiWindowSelfTest drives these same lines with a writer it has fed an unattributable
        /// behaviour event, since no converted pack reaches one (it passes no random: such a writer drew nothing).</summary>
        internal static int Finish(ParityTraceWriter writer, int ticks, string status, ParityRandom random, TextWriter log)
        {
            if (writer.ProvenanceFault != null)
            {
                status = "error";
                log.WriteLine("parity trace: " + writer.ProvenanceFault);
            }
            // CONTRACT 10.8: exhaustion leaves the status alone and is still logged, as information. It is read from the
            // draws the run actually made, so an aborted or failed run reports it too, for what it drew before it stopped.
            bool tapeExhausted = random != null && random.TapeExhausted;
            if (tapeExhausted)
                log.WriteLine("rng-tape-exhausted (information): the run drew past the end of its " + random.TapeLength
                    + "-value tape; each draw past the end yielded 0.5 and the run went on");
            writer.Footer(ticks, status, tapeExhausted);
            return status == "complete" ? 0 : 1;
        }

        private static int CompareUtf8(string a, string b)
        {
            byte[] x = Encoding.UTF8.GetBytes(a), y = Encoding.UTF8.GetBytes(b);
            for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
                if (x[i] != y[i]) return x[i] - y[i];
            return x.Length - y.Length;
        }

        public static string Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                var b = new StringBuilder(64);
                foreach (byte x in sha.ComputeHash(data)) b.Append(x.ToString("x2", CultureInfo.InvariantCulture));
                return b.ToString();
            }
        }

        /// <summary>CONTRACT section 3: '&lt;sha256&gt;  &lt;relative path&gt;\n' per file, sorted by the paths' UTF-8 bytes.
        /// The pack is walked without following links, and a link anywhere in it (the directory included) is refused
        /// before any file is read: only a link can lead a pack path outside the pack.</summary>
        public static string PackSha256(string packDir)
        {
            if (IsLink(packDir)) throw new InvalidDataException("pack-invalid: the pack " + packDir + " is a link");
            var files = new List<string>();
            var dirs = new Stack<string>();
            dirs.Push(packDir);
            while (dirs.Count > 0)
            {
                string dir = dirs.Pop();
                foreach (string d in Directory.GetDirectories(dir))
                {
                    if (IsLink(d)) throw new InvalidDataException("pack-invalid: the pack holds a link: " + Relative(packDir, d));
                    dirs.Push(d);
                }
                foreach (string f in Directory.GetFiles(dir))
                {
                    if (IsLink(f)) throw new InvalidDataException("pack-invalid: the pack holds a link: " + Relative(packDir, f));
                    files.Add(f);
                }
            }
            var rows = new List<KeyValuePair<string, string>>();
            foreach (string file in files)
                rows.Add(new KeyValuePair<string, string>(Relative(packDir, file), Sha256(File.ReadAllBytes(file))));
            return ManifestSha256(rows);
        }

        /// <summary>CONTRACT section 3, converterSha256: the converter's CODE, as a manifest in the pack form over its
        /// managed assemblies when given a directory (every *.dll in it, never an apphost, whose bytes do not change with
        /// the code), or over the one file given (a script).</summary>
        public static string ConverterSha256(string path)
        {
            string[] files = Directory.Exists(path) ? Directory.GetFiles(path, "*.dll") : new[] { path };
            var rows = new List<KeyValuePair<string, string>>();
            foreach (string f in files) rows.Add(new KeyValuePair<string, string>(Path.GetFileName(f), Sha256(File.ReadAllBytes(f))));
            return ManifestSha256(rows);
        }

        private static string ManifestSha256(List<KeyValuePair<string, string>> rows)
        {
            // By UTF-8 bytes, as the comparer sorts; UTF-16 order differs for supplementary characters.
            rows.Sort((x, y) => CompareUtf8(x.Key, y.Key));
            var text = new StringBuilder();
            foreach (KeyValuePair<string, string> r in rows) text.Append(r.Value).Append("  ").Append(r.Key).Append('\n');
            return Sha256(Encoding.UTF8.GetBytes(text.ToString()));
        }

        private static bool IsLink(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        private static string Relative(string root, string path)
        {
            return path.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
        }
    }
}
