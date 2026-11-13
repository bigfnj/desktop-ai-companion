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
            // Environment.getScreenRect: the bounding rectangle of every screen. ParityRun.ScenarioInvalid refuses a scenario
            // with none, for which these starting values would be the rectangle (N-bd-w11-shimeji-host-1-02).
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

        /// <summary>A14 (signed revision H :136): the scripted desktop window as it is now, read from the private
        /// TickDesktopWindow with its per-kind success counters, for ParityRun.Run's reconciliation ledger. Never
        /// ActiveWindow: that is the area the companions read, refreshed only at the next tick, so a move made this tick
        /// would not show in it yet.</summary>
        internal ParityWindowState DesktopWindow
        {
            get { return new ParityWindowState(_desktopWindow.Shown, _desktopWindow.Minimised, _desktopWindow.Rect, _desktopWindow.Moves, _desktopWindow.Minimises); }
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

        /// <summary>
        /// Called with every line this writer commits: each event line from its sink (a trace-off run forms and folds it
        /// too, only not writing it) and each state line, after it is written. ParityRun.Run's reconciliation ledger reads
        /// the exact committed records here (signed revision H :138, :142); null otherwise. A settable property, so the
        /// constructor stays as it was, and not an ITickObserver member, so no observer and no trace line changes.
        /// </summary>
        internal Action<string> Committed { get { return _out.Committed; } set { _out.Committed = value; } }

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

            /// <summary>Where each committed line also goes (<see cref="ParityTraceWriter.Committed"/>); null changes nothing.</summary>
            public Action<string> Committed;

            public void WriteLine(string line)
            {
                _digest.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
                if (_output != null) _output.WriteLine(line);
                // After the digest and the write, so a proof is handed exactly the line committed, in commit order, and the
                // sink's output and digest are what they were without it (round 4's differential compares
                // eventSequenceSha256).
                Action<string> committed = Committed;
                if (committed != null) committed(line);
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

        /// <param name="runId">The invocation's one runId, made by ParityRun.Run, which logs it on every closure line of the
        /// same run (signed revision H :150, :158), so a log line and its trace are bound by it.</param>
        public void Header(string scenarioId, string scenarioSha, string packSha, Dictionary<string, string> build, string rngJson, double dpiScale, string runId)
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
                .Str("runId", runId).Str("startedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
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
            string line = w.End();
            _trace.WriteLine(line);
            _states++;
            Action<string> committed = _out.Committed;
            if (committed != null) committed(line);
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

        /// <summary>CONTRACT 10.3: the message in the engine's own words, byte for byte, and the typed error object beside
        /// it. The code and its operands are what the engine formed where the fault arose (<see cref="TickFaultCause"/>);
        /// this writer only spells them, and never reads the message to choose one.</summary>
        public void Fault(TickMascot mascot, string message, TickFaultCause cause, string via)
        {
            _out.WriteLine(Event(mascot, "error").Str("message", message).Raw("error", ErrorObject(cause, via)).End());
        }

        /// <summary>The 10.3.1 object: code, lookup (exactly the code's operand set of 10.3.2, or null for unclassified)
        /// and via (the wrapper that caught the fault, outermost first; empty when the step caught it or it was reported
        /// where it arose).</summary>
        internal static string ErrorObject(TickFaultCause cause, string via)
        {
            string lookup = "null";
            if (cause != null)
            {
                var l = new JsonLine();
                switch (cause.Code)
                {
                    case TickFaultCause.BehaviorMissing: l.Str("imageSet", cause.ImageSet).Str("behavior", cause.Behavior); break;
                    case TickFaultCause.ActionMissing: l.Str("imageSet", cause.ImageSet).Str("action", cause.Action); break;
                    case TickFaultCause.ParameterInvalid: l.Str("refKey", cause.RefKey).Str("parameter", cause.Parameter); break;
                    default: l.Str("refKey", cause.RefKey); break;   // variant-missing, scan-target-missing
                }
                lookup = l.End();
            }
            return new JsonLine().Str("code", cause == null ? TickFaultCause.Unclassified : cause.Code).Raw("lookup", lookup)
                .StrList("via", via == null ? new string[0] : new[] { via }).End();
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
            return Execute(args, at, log, stamped, AppPaths.IsDataRootOverridden);
        }

        /// <param name="isolated">Whether this process runs under an overridden test data root: AppPaths' answer, except in
        /// ShimejiWindowSelfTest, which passes true to reach the converter refusals on every run (N-bd-w12-review-02). They
        /// come after this gate, so without a test data root (CI, an unisolated gate) they were not exercised at all and
        /// said so only in the marker. Its other legs call ParityRun.Run directly, which has no such gate.</param>
        internal static int Execute(string[] args, int at, TextWriter log, System.Reflection.Assembly stamped, bool isolated)
        {
            if (args.Length < at + 5)
            {
                log.WriteLine("usage: " + Flag + " <scenario.json> <pets dir> <trace out> <converter exe>");
                return 2;
            }
            // N-bd-w12-review-03, as the coordinator decided after two follow-ups: a trace path where anything already is (a
            // file, a folder, a link, dangling or not) is refused by name with exit 2 before every other refusal, and nothing
            // at it is ever deleted, truncated or replaced (ParityRun.TraceClaim says why). So "exit 2, no trace" holds for a
            // reused path too: what is there was never this run's, and the refusal says so. The claim also keeps a NEW
            // trace out of every input folder, and the scenario's pack is an input folder only the scenario names, so the
            // scenario is read first, once; the run below is handed these same bytes rather than reading the file again, so
            // the pack it hashes is the pack the claim compared against. The claim only reads, so it may come ahead of the
            // gates, which now have nothing at the trace path to protect.
            ParityRun.ScenarioFile scenario = ParityRun.ScenarioFile.Read(args[at + 1]);
            ParityRun.TraceClaim claim = ParityRun.TraceClaim.Claim(scenario, args[at + 2], args[at + 3], args[at + 4], log);
            if (claim == null) return 2;
            // The build's identity next, before the data-root gate and before anything else is read or a trace opened. A
            // missing or malformed revision is refused rather than written as a placeholder, which the comparer's
            // non-empty check would pass while it names nothing (contract review item 13), and it is never replaced by the
            // product version (N-bd-parity-host-1-03).
            string refusal;
            string commit = SourceRevision(stamped, out refusal);
            if (commit == null)
            {
                log.WriteLine(Flag + " refused: " + refusal + ". A parity trace names the source revision its build came from;"
                    + " rebuild from a git checkout.");
                return 2;
            }
            if (!isolated)
            {
                log.WriteLine(Flag + " runs only under an overridden test data root (" + AppPaths.DataRootOverrideEnvironmentVariable + ").");
                return 2;
            }
            // N-bd-w11-shimeji-host-1-01: the converter is the first path argument read (its hash is the build witness's
            // last field), and one that names nothing is refused by name with exit 2, as the run's own path arguments are.
            // Unchecked, a converter that did not exist threw FileNotFoundException out of the command.
            string converter = args[at + 4], converterSha256;
            try
            {
                converterSha256 = ParityRun.ConverterSha256(converter);
            }
            catch (Exception e) when (ParityRun.IsArgumentFault(e))
            {
                log.WriteLine("refused " + converter + ": the converter cannot be read: " + e.Message);
                return 2;
            }
            catch (InvalidDataException e)
            {
                // N-bd-w12-shimeji-host-1-03: a converter folder that holds no code (ConverterSha256 says why).
                log.WriteLine("refused " + converter + ": " + e.Message);
                return 2;
            }
            // N-parity-1107-03 (signed revision H :78-80): the EXE and engine hashes are this process's own reads of the files
            // it loaded, through ParityLoadedEngine, which ParityRun.Run then logs. One that cannot be read (a null
            // ProcessPath, an empty Location, a file that cannot be opened) is the named build-witness-unreadable refusal,
            // exit 2 and no trace. Read bare, as it was, each threw out of the command.
            ParityLoadedEngine engine = ParityLoadedEngine.Read(ParityEnginePaths.Process, out refusal);
            if (engine == null)
            {
                log.WriteLine(refusal);
                return 2;
            }
            var build = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "commit", commit },
                { "exeSha256", engine.ExeSha256 },
                { "engineSha256", engine.EngineSha256 },
                { "converterSha256", converterSha256 },
            };
            return ParityRun.Run(scenario, args[at + 2], args[at + 3], build, log, false, engine, false);
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
        /// and exit 2, never an unhandled exception out of the command. A scenario's readers throw the same kinds, and
        /// <see cref="ReadScenario"/> refuses them as scenario-invalid.</summary>
        private static bool IsRefusal(Exception e)
        {
            return e is InvalidDataException || e is JsonException || e is FormatException || e is InvalidOperationException
                || e is KeyNotFoundException || e is OverflowException;
        }

        /// <summary>N-bd-w11-shimeji-host-1-01: what reading or opening one of the command's path arguments throws when the
        /// path names nothing usable (no such file or folder, a folder where a file is wanted, no access, an empty or
        /// malformed path). Each is the caller's argument error, refused by name with exit 2 and no trace, never an
        /// unhandled exception out of the command. Caught around the one call that touches the argument, so nothing else
        /// the run does is taken for an argument error.</summary>
        internal static bool IsArgumentFault(Exception e)
        {
            return e is IOException || e is UnauthorizedAccessException || e is ArgumentException;
        }

        /// <summary>
        /// What is already at the trace path, in words for the refusal ("a file", "a folder", "a link"), or null when
        /// nothing is. File.GetAttributes reads the entry itself and does not follow it, so a link is seen as a link and a
        /// dangling one too, and .NET hands it a path past 260 characters in the form Windows accepts. Null also when no
        /// entry can be read at the path at all (no such file or folder, a missing or non-folder parent, an empty or
        /// malformed path, an entry whose attributes this account may not read): nothing seen there is replaced either
        /// way, because the trace is opened only as a new file (FileMode.CreateNew in the run), which refuses anything that
        /// is there after all.
        /// </summary>
        private static string OccupantOf(string tracePath)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(tracePath);
            }
            catch (Exception e) when (IsArgumentFault(e) || e is NotSupportedException)
            {
                return null;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0) return "a link";
            if ((attributes & FileAttributes.Directory) != 0) return "a folder";
            return "a file";
        }

        /// <summary>The refusal of a trace path something is already at (<see cref="OccupantOf"/>'s words), from the claim
        /// and from the open, so a caller reads one line for both.</summary>
        private static string AlreadyThere(string tracePath, string occupant)
        {
            return "refused " + tracePath + ": " + occupant + " is already there and a parity run never replaces one;"
                + " delete it or name a new trace path";
        }

        /// <summary>The open's own answer for a file that is already there: ERROR_FILE_EXISTS (80) from CREATE_NEW, or
        /// ERROR_ALREADY_EXISTS (183).</summary>
        private static bool IsAlreadyThere(IOException e)
        {
            int code = e.HResult & 0xFFFF;
            return code == 80 || code == 183;
        }

        /// <summary>The scenario file as the run reads it, once: its bytes, or the argument fault reading them threw. Read
        /// before the trace path is claimed, because only its bytes name the pack, and refused (the fault) only after the
        /// claim, so a trace path something is already at is named first.
        ///
        /// N-parity-1107-03 C7 (signed revision H :45, :56-62, :230): the full SHA-256 of these retained bytes and the pure
        /// candidate scan facts are taken here, at the single read, before the claim and before anything parses the bytes;
        /// the header reuses this digest. Scan facts alone never refuse anything (H :45-52): the run routes a candidate to
        /// the registry drift self-test only after the claim and the command's prerequisites.</summary>
        internal sealed class ScenarioFile
        {
            public readonly string Argument;
            public readonly byte[] Bytes;
            public readonly Exception Fault;
            public readonly string Sha256;
            public readonly ParityScenarioScan.Result Scan;

            private ScenarioFile(string argument, byte[] bytes, Exception fault)
            {
                Argument = argument;
                Bytes = bytes;
                Fault = fault;
                if (bytes != null)
                {
                    Sha256 = ParityRun.Sha256(bytes);
                    Scan = ParityScenarioScan.Scan(bytes);
                }
            }

            internal static ScenarioFile Read(string scenarioPath)
            {
                try
                {
                    return new ScenarioFile(scenarioPath, File.ReadAllBytes(scenarioPath), null);
                }
                catch (Exception e) when (IsArgumentFault(e))
                {
                    return new ScenarioFile(scenarioPath, null, e);
                }
            }
        }

        /// <summary>
        /// N-bd-w12-review-03, as the coordinator decided: a trace path where nothing is, and where a new file would be in
        /// none of the run's input folders. "Exit 2, no trace" held only for a fresh path: every refusal left an existing
        /// file as it was, and a caller that reused the path read the last run's trace as this one's. The first fix
        /// released (deleted) an existing file at the trace path ahead of the refusals, and that deletion had to be kept
        /// off the run's own inputs by comparing paths. Two follow-ups did so and each was refuted with a new spelling of an
        /// input (a junction inside an input folder; then a path past 260 characters, where the resolution fell back to the
        /// spelling in silence, and by code a loopback share), because no comparison of spellings can know every name
        /// Windows gives one file. So nothing is deleted any more. <see cref="Claim"/> refuses a trace path where ANYTHING
        /// already is (<see cref="OccupantOf"/>), by name with exit 2, before every other refusal of the command and of
        /// the run, and the run opens its trace only as a new file (FileMode.CreateNew), so a file that appears between
        /// the claim and the open is refused there and is never truncated or replaced either. An input is a file that
        /// exists, so a trace path that is an input, however it is spelled, is refused as already there. The rejected
        /// alternative was the release, for the reason above; its cost, that a caller rerunning into one path now gets
        /// exit 2, is paid by the callers (each in-repository caller uses a fresh path or deletes its own old trace).
        /// What the claim still compares is where a NEW trace would go (<see cref="InputFolderAtTracePath"/>), as defence
        /// in depth only. The claim is a token: only <see cref="Claim"/> makes one, and it holds nothing to release.
        /// </summary>
        internal sealed class TraceClaim
        {
            private TraceClaim()
            {
            }

            /// <summary>
            /// The claim, or null with the refusal logged: first when anything is already at the trace path, then when a
            /// new trace there would be inside an input folder (<see cref="InputFolderAtTracePath"/> says which). Inside the
            /// pack matters even for a new file: the pack is hashed before the trace is opened, so a trace written into it
            /// leaves a pack that no longer matches the hash in the header. The pack comes from <paramref name="scenario"/>'s
            /// bytes, so the scenario is read before this, and its "pack" is read even when the scenario will be refused for
            /// something else: the folder it names is still the caller's. The converter is null for the in-process callers,
            /// which have none. A path the comparison could not resolve is said on the log as DEGRADED, every time, ahead of
            /// the claim's answer: that comparison then ran on the path as written only. So is a trace path that resolves to
            /// a share, which the comparison cannot see behind (<see cref="InputFolderAtTracePath"/>).
            ///
            /// N-bd-w13-review-09: a trace path whose final name holds a colon is refused right after the occupant check.
            /// On Windows a colon there is no part of a name: scenario.json:t names the data stream t of scenario.json, so
            /// a trace written there goes INTO that file or folder (its content as it was, its time stamp moved), and the
            /// stream is new, so neither the occupant check nor the folder comparison saw it. Refused whatever the file or
            /// folder before the colon is, input or not: a trace is a file of its own (<see cref="StreamOwnerOf"/>).
            /// </summary>
            internal static TraceClaim Claim(ScenarioFile scenario, string petsDir, string tracePath, string converter, TextWriter log)
            {
                string occupant = OccupantOf(tracePath);
                if (occupant != null)
                {
                    log.WriteLine(AlreadyThere(tracePath, occupant));
                    return null;
                }
                string streamOwner = StreamOwnerOf(tracePath);
                if (streamOwner != null)
                {
                    log.WriteLine("refused " + tracePath + ": the colon in its name names a data stream of " + streamOwner
                        + ", not a file of its own; a parity run writes its trace only as a new file");
                    return null;
                }
                var unresolved = new List<string>();
                string share;
                string input = InputFolderAtTracePath(scenario, petsDir, tracePath, converter, unresolved, out share);
                foreach (string path in unresolved)
                    log.WriteLine("DEGRADED: parity run: " + path + " could not be resolved through its links, so the trace path is compared"
                        + " with the input folders there as written only");
                if (share != null)
                    log.WriteLine("DEGRADED: parity run: the trace path resolves to the share " + share + ", so it is compared with the input"
                        + " folders as a share only: an input folder reached through a share that leads back to this machine (a loopback"
                        + " name, or a drive mapped to one) is not seen as the folder it is");
                if (input != null)
                {
                    log.WriteLine("refused " + tracePath + ": the trace path is " + input + "; a parity run never writes into its inputs");
                    return null;
                }
                return new TraceClaim();
            }
        }

        /// <summary>
        /// Which input folder a new trace at the trace path would be inside, in words for the refusal, or null. The input
        /// folders are those the run reads from: the converter (when it is a folder), the pets folder, each pet folder in it
        /// that LoadPets reads (<see cref="PetFoldersRead"/>), and the scenario's pack folder. Input FILES are not compared:
        /// each exists, so a trace path that is one is refused as already there (<see cref="OccupantOf"/>) whatever its
        /// spelling. Each folder is compared on both spellings of both sides (<see cref="Spellings"/>): as written, a trace
        /// pets\Ball\x is inside the pets folder whatever pets\Ball links to; as resolved, a trace spelled as the folder a
        /// junction pet folder leads to is inside that pet folder. A pet folder is resolved on its own for that reason
        /// (follow-up 2: resolving only the arguments followed the junction out of the pets folder). A link inside an
        /// input folder that leads somewhere the run never reads (a subfolder of the converter folder, a link inside a pet
        /// folder) is not followed, though a trace path SPELLED through it is still inside that folder and refused. An
        /// input that names nothing (an empty or malformed argument, a folder that cannot be listed) adds nothing here and
        /// is refused later by its own line.
        ///
        /// What this comparison does NOT catch, said plainly: a path through a share that leads back to this machine
        /// (\\localhost\D$\..., \\127.0.0.1\..., or a drive letter mapped to such a share), which Windows resolves to the
        /// share's \\server\share form and never to the local path; and an existing part of a path no handle can be opened
        /// on (no access, a dangling link) or whose attributes cannot be read, which is then compared as written. Each is
        /// said on the log as DEGRADED, every time (N-bd-w13-review-09 added the share, whose spelling of the trace path
        /// <paramref name="share"/> answers, the part that cannot be read, and a path no part of which exists). That is
        /// now harmless, because nothing that exists is ever replaced (the claim refuses an occupied path and the open
        /// creates only a new file): the worst a spelling this misses can do is put a NEW file inside an input folder, one
        /// the next run's pack hash or pets listing then sees, never change or remove a file the run read.
        /// </summary>
        private static string InputFolderAtTracePath(ScenarioFile scenario, string petsDir, string tracePath, string converter, List<string> unresolved, out string share)
        {
            // A trace path no full path can be made of names no file: nothing can be put inside an input folder there, and
            // opening it is refused below as an unwritable trace, as it was.
            share = null;
            string[] trace = Spellings(tracePath, unresolved);
            if (trace == null) return null;
            share = ShareOf(trace);
            string named;
            if (converter != null && (named = Overlap(trace, converter, "the converter ", unresolved)) != null) return named;
            if ((named = Overlap(trace, petsDir, "the pets folder ", unresolved)) != null) return named;
            foreach (string pet in PetFoldersRead(petsDir))
                if ((named = Overlap(trace, pet, "the pet folder ", unresolved)) != null) return named;
            string pack = PackFolderOf(scenario);
            if (pack != null && (named = Overlap(trace, pack, "the scenario's pack folder ", unresolved)) != null) return named;
            return null;
        }

        /// <summary>"inside <paramref name="what"/><paramref name="input"/>" when the trace is anywhere under the input
        /// folder, else null. Every spelling of the trace against every spelling of the folder (<see cref="Spellings"/>).
        /// The way Windows compares names: case-insensitive (ordinal), with no trailing separator; under means the
        /// folder's path plus a separator, so a sibling whose name only starts the same ("pets2" beside "pets") is not
        /// inside it. A trace path that IS the folder is already there (<see cref="OccupantOf"/>), or names a folder
        /// that does not exist, which its own line refuses before any trace is opened.</summary>
        private static string Overlap(string[] trace, string input, string what, List<string> unresolved)
        {
            string[] spellings = Spellings(input, unresolved);
            if (spellings == null) return null;
            foreach (string t in trace)
                foreach (string i in spellings)
                {
                    string under = i[i.Length - 1] == Path.DirectorySeparatorChar ? i : i + Path.DirectorySeparatorChar;
                    if (t.StartsWith(under, StringComparison.OrdinalIgnoreCase)) return "inside " + what + input;
                }
            return null;
        }

        /// <summary>A path's two spellings for <see cref="Overlap"/>: as written, made full by Path.GetFullPath (relative
        /// parts, "." and "..", "/", trailing dots and spaces) with no trailing separator and every link along it left
        /// where it stands; and as Windows resolves it (<see cref="ResolvedPath"/>). Null when no full path can be made of
        /// it.</summary>
        private static string[] Spellings(string path, List<string> unresolved)
        {
            string written = FullPathOrNull(path);
            if (written == null) return null;
            return new[] { written, ResolvedPath(written, unresolved) };
        }

        /// <summary>The trace's spelling that names a share, the resolved one first (<see cref="Spellings"/> puts it
        /// second), or null when neither does (N-bd-w13-review-09). The resolved one is what a drive mapped to a share
        /// becomes; the written one stands when no part of a share path exists (an unknown server or share).</summary>
        private static string ShareOf(string[] spellings)
        {
            for (int i = spellings.Length - 1; i >= 0; i--)
                if (IsShare(spellings[i])) return spellings[i];
            return null;
        }

        /// <summary>A share path: \\server\share..., or its device form \\?\UNC\ or \\.\UNC\. Any other \\?\ or \\.\ path
        /// is a local device path (a drive, a volume, the pipe namespace), not a share.</summary>
        private static bool IsShare(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
            return path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal);
        }

        /// <summary>The file or folder a trace path's final name puts a data stream on ("scenario.json:t" is the stream t of
        /// scenario.json), or null when that name holds no colon (<see cref="TraceClaim.Claim"/> says why it is refused).
        /// Read on the full path, so "." and ".." and a trailing separator are gone and a drive's own colon (D:, \\?\D:) is
        /// in the root, not in the name. Only the final name. A colon in a folder part mostly names a stream, which no file
        /// can be made under, and the open refuses that path as unwritable. On NTFS one such part is the folder itself:
        /// "pets::$INDEX_ALLOCATION" (or "pets:$I30:$INDEX_ALLOCATION") names the pets folder's own index, so a new file
        /// can be made through it, inside pets. Nothing is added here for that: ResolvedPath opens the existing part
        /// (File.GetAttributes reads it as the folder), GetFinalPathNameByHandle answers the real folder, and Overlap
        /// compares that with the input folders, so a trace spelled through pets::$INDEX_ALLOCATION is refused as inside
        /// the pets folder. Where that part cannot be read or opened, the claim says so as DEGRADED (the unresolved note)
        /// and compares the path as written.</summary>
        private static string StreamOwnerOf(string tracePath)
        {
            string full = FullPathOrNull(tracePath);
            if (full == null) return null;
            string root = Path.GetPathRoot(full) ?? "";
            if (full.Length <= root.Length) return null;
            string name = Path.GetFileName(full);
            int colon = name.IndexOf(':');
            if (colon < 0) return null;
            return full.Substring(0, full.Length - name.Length + colon);
        }

        /// <summary>
        /// The pet folders LoadPets reads: every folder in the pets folder, listed as LoadPets lists it, that holds an
        /// animations.xml or a parity-map.json (the folders it loads or refuses; one with neither it skips and reads
        /// nothing in). Each is resolved on its own, so a pet folder that is a junction protects where its files really
        /// are. Empty when the pets folder cannot be listed (LoadPets refuses it by its own line).
        /// </summary>
        private static List<string> PetFoldersRead(string petsDir)
        {
            var read = new List<string>();
            string[] folders;
            try
            {
                folders = Directory.GetDirectories(petsDir);
            }
            catch (Exception e) when (IsArgumentFault(e))
            {
                return read;
            }
            foreach (string folder in folders)
                if (File.Exists(Path.Combine(folder, "animations.xml")) || File.Exists(Path.Combine(folder, "parity-map.json"))) read.Add(folder);
            return read;
        }

        /// <summary>The scenario's pack folder from the bytes the run will read, or null when they name none (unreadable,
        /// not JSON, no "pack" string, or one no path can be made of: each refused later by its own line).</summary>
        private static string PackFolderOf(ScenarioFile scenario)
        {
            if (scenario.Bytes == null) return null;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(scenario.Bytes))
                {
                    JsonElement pack;
                    if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("pack", out pack)
                        || pack.ValueKind != JsonValueKind.String)
                        return null;
                    // N-parity-1107-03 C6 (H :45): a pack string with an escaped unpaired surrogate cannot be decoded, so
                    // it names no usable pack folder; the claim goes on without one and the run refuses the scenario by
                    // its own line. Caught round the decode only, so nothing else here is taken for it.
                    string packText;
                    try
                    {
                        packText = pack.GetString();
                    }
                    catch (InvalidOperationException)
                    {
                        return null;
                    }
                    return PackFolder(scenario.Argument, packText);
                }
            }
            catch (JsonException)
            {
                return null;
            }
            catch (Exception e) when (IsArgumentFault(e))
            {
                return null;
            }
        }

        /// <summary>CONTRACT section 1: the pack, relative to the scenario's own folder. One definition for the claim and
        /// the run, so the folder the claim protects is the folder the run hashes.</summary>
        private static string PackFolder(string scenarioPath, string pack)
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scenarioPath)), pack));
        }

        /// <summary>A path made full by Path.GetFullPath, with no trailing separator, or null when no full path can be made of
        /// it (empty, a NUL, a malformed name).</summary>
        private static string FullPathOrNull(string path)
        {
            try
            {
                return WithoutTrailingSeparator(Path.GetFullPath(path));
            }
            catch (Exception e) when (IsArgumentFault(e) || e is NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>
        /// A full path (<see cref="FullPathOrNull"/>'s) as Windows resolves it, for comparing two of them: the deepest part
        /// that exists resolved through a handle by GetFinalPathNameByHandle (8.3 short names to long ones, every junction
        /// and symbolic link along it), with the part that does not exist yet put back after it and any trailing separator
        /// taken off. A trace path rarely exists, and a new file under an existing folder is still under that folder's real
        /// path. A share, a loopback one included, resolves to its \\server\share form (<see cref="InputFolderAtTracePath"/>
        /// says why that is harmless now). Where no handle can be opened (no access, a dangling link) the existing part
        /// stands as written and is added to <paramref name="unresolved"/>, which the claim says on its log as DEGRADED:
        /// two spellings only that resolution would join are then compared as written, and the log says so every time.
        /// N-bd-w13-review-09: so is a part whose attributes cannot be read (no access to it or its folder, a malformed
        /// name), which File.Exists and Directory.Exists answered as absent, so the walk went past it in silence and put
        /// it back as written; and a path no part of which exists (no such drive, server or share), which came back as
        /// written with no note. The walk itself is as it was: such a part is still passed and put back as written.
        /// </summary>
        private static string ResolvedPath(string full, List<string> unresolved)
        {
            string existing = full;
            var missing = new Stack<string>();
            int unread;
            while (!PartExists(existing, out unread))
            {
                if (unread != 0) Note(unresolved, existing + " (its attributes could not be read: Win32 error " + unread.ToString(CultureInfo.InvariantCulture) + ")");
                string parent = Path.GetDirectoryName(existing);
                if (parent == null)
                {
                    Note(unresolved, full + " (no part of it exists)");
                    return full;
                }
                missing.Push(Path.GetFileName(existing));
                existing = parent;
            }
            int error;
            string resolved = FinalPathOrNull(existing, out error);
            if (resolved == null)
            {
                string note = existing + " (Win32 error " + error.ToString(CultureInfo.InvariantCulture) + ")";
                if (!unresolved.Contains(note)) unresolved.Add(note);
                resolved = existing;
            }
            foreach (string name in missing) resolved = Path.Combine(resolved, name);
            return WithoutTrailingSeparator(resolved);
        }

        /// <summary>
        /// Whether a part of a path is there, read as <see cref="OccupantOf"/> reads the trace path (File.GetAttributes,
        /// the entry itself, so a dangling link is there). False with <paramref name="unread"/> 0 when Windows says the
        /// name is not there: no such file or folder, no such drive (DriveNotFoundException), no such server or share
        /// (ERROR_BAD_NETPATH 53, ERROR_BAD_NETNAME 67). False with the Win32 error in <paramref name="unread"/> when the
        /// attributes could not be read at all (ERROR_ACCESS_DENIED 5 where neither the entry nor its folder may be read,
        /// ERROR_INVALID_NAME 123 for a malformed one): not known to be absent, which the caller says.
        /// </summary>
        private static bool PartExists(string path, out int unread)
        {
            unread = 0;
            try
            {
                File.GetAttributes(path);
                return true;
            }
            catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException || e is DriveNotFoundException)
            {
                return false;
            }
            catch (Exception e) when (IsArgumentFault(e) || e is NotSupportedException)
            {
                // A Win32 error is carried as 0x8007xxxx; anything else (no Win32 error behind it) is said as -1.
                int code = (e.HResult & 0x1FFF0000) == 0x00070000 ? e.HResult & 0xFFFF : -1;
                if (code != 53 && code != 67) unread = code == 0 ? -1 : code;
                return false;
            }
        }

        /// <summary>Adds one DEGRADED note for the claim's log, once however many spellings reach it.</summary>
        private static void Note(List<string> unresolved, string note)
        {
            if (!unresolved.Contains(note)) unresolved.Add(note);
        }

        private static string WithoutTrailingSeparator(string full)
        {
            string root = Path.GetPathRoot(full);
            while (full.Length > (root == null ? 0 : root.Length) && (full[full.Length - 1] == Path.DirectorySeparatorChar || full[full.Length - 1] == Path.AltDirectorySeparatorChar))
                full = full.Substring(0, full.Length - 1);
            return full;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern int GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle handle, StringBuilder path, int count, int flags);

        /// <summary>
        /// The form of a full path CreateFileW takes past 260 characters: \\?\ before a drive path, \\?\UNC\ in place of a
        /// share's leading \\, and \\?\ in place of \\.\ (a path already in \\?\ form stands). This process is not
        /// longPathAware (the apphost's manifest does not say so), so CreateFileW refuses a longer path given plainly,
        /// whatever the machine's LongPathsEnabled; .NET's own file calls add this prefix themselves, which is why the run
        /// could read and write a path its resolution could not open (the wave 13 fix-2 review: a pets folder at a 260
        /// character path whose pet folder was a junction, and a trace spelled as the junction's target). The \\?\ form
        /// turns off Windows' own normalising, which is safe here because the path is already Path.GetFullPath's.
        /// </summary>
        private static string LongPathForm(string full)
        {
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
            if (full.StartsWith(@"\\.\", StringComparison.Ordinal)) return @"\\?\" + full.Substring(4);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + full.Substring(2);
            return @"\\?\" + full;
        }

        /// <summary>Where an existing file or folder really is (OptionsWindow.FinalDirectoryPath's call): no access asked,
        /// every share mode so a file another process holds still resolves, FILE_FLAG_BACKUP_SEMANTICS so a folder opens,
        /// the path handed over in its long form (<see cref="LongPathForm"/>), and the \\?\ prefix taken off the answer
        /// (\\?\UNC\ back to \\) so it compares like with like. Null, with the Win32 error in <paramref name="error"/>,
        /// when it cannot be resolved.</summary>
        private static string FinalPathOrNull(string existing, out int error)
        {
            const uint shareAll = 0x1 | 0x2 | 0x4, openExisting = 3, backupSemantics = 0x02000000;
            error = 0;
            using (Microsoft.Win32.SafeHandles.SafeFileHandle handle = CreateFileW(LongPathForm(existing), 0, shareAll, IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero))
            {
                if (handle == null || handle.IsInvalid)
                {
                    error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    return null;
                }
                var buffer = new StringBuilder(512);
                int written = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
                if (written > buffer.Capacity)
                {
                    buffer = new StringBuilder(written);
                    written = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
                }
                if (written <= 0 || written > buffer.Capacity)
                {
                    error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    return null;
                }
                string final = buffer.ToString(0, written);
                if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + final.Substring(8);
                if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) return final.Substring(4);
                return final;
            }
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
            string[] petDirs;
            try
            {
                petDirs = Directory.GetDirectories(petsDir);
            }
            catch (Exception e) when (IsArgumentFault(e))
            {
                log.WriteLine("refused " + petsDir + ": the pets folder cannot be listed: " + e.Message);
                return null;
            }
            foreach (string dir in petDirs)
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
            return Run(scenarioPath, petsDir, tracePath, build, log, traceOff, ParityEnginePaths.Process);
        }

        /// <param name="paths">Where this process's own EXE and engine are: ParityEnginePaths.Process, except in
        /// ShimejiWindowSelfTest, which hands it an empty engine Location to see the build-witness-unreadable refusal.</param>
        internal static int Run(string scenarioPath, string petsDir, string tracePath, Dictionary<string, string> build, TextWriter log, bool traceOff,
            ParityEnginePaths paths)
        {
            // N-bd-w12-review-03: claimed here too, for the callers that come in below the command (the window self-test),
            // in the command's order (ParityRunCommand.Execute says why): the scenario read once, then a trace path that
            // anything is already at, or that is inside an input folder, refused ahead of every other refusal.
            ScenarioFile scenarioFile = ScenarioFile.Read(scenarioPath);
            if (TraceClaim.Claim(scenarioFile, petsDir, tracePath, null, log) == null) return 2;
            return Run(scenarioFile, petsDir, tracePath, build, log, traceOff, paths);
        }

        /// <summary>A run below the command on a scenario already read and a trace path already claimed: the overload above,
        /// and ShimejiWindowSelfTest's race leg, which writes a file between its claim and this run's open.</summary>
        internal static int Run(ScenarioFile scenarioFile, string petsDir, string tracePath, Dictionary<string, string> build, TextWriter log, bool traceOff,
            ParityEnginePaths paths = null)
        {
            // N-parity-1107-03 (H :80): a caller below the command supplies the build values itself, so this run is labelled
            // synthetic, and it still reads and logs the engine this process actually loaded, refusing as the command does
            // when it cannot.
            string refusal;
            ParityLoadedEngine engine = ParityLoadedEngine.Read(paths ?? ParityEnginePaths.Process, out refusal);
            if (engine == null)
            {
                log.WriteLine(refusal);
                return 2;
            }
            return Run(scenarioFile, petsDir, tracePath, build, log, traceOff, engine, true);
        }

        /// <summary>The run on a scenario already read and a trace path already claimed (<see cref="TraceClaim"/>), which
        /// it opens only as a new file.</summary>
        /// <param name="engine">The EXE and engine this process loaded, read by the caller before anything is opened.</param>
        /// <param name="synthetic">True when the caller is not ParityRunCommand: the build values were supplied, not read
        /// by this process, and the run says so (H :80, :187).</param>
        internal static int Run(ScenarioFile scenarioFile, string petsDir, string tracePath, Dictionary<string, string> build, TextWriter log, bool traceOff,
            ParityLoadedEngine engine, bool synthetic)
        {
            string scenarioPath = scenarioFile.Argument;
            // N-parity-1107-03, Codex's closure ruling Q-c (H :150): the run's one runId is made here, at the core run's
            // entry, and the engine this process loaded (read by the caller, which refused build-witness-unreadable ahead
            // of this) is logged at once, before the scenario is validated and before the trace is opened. So a run that
            // got this far and is then refused (scenario-input-unreadable, scenario-invalid, a pet, the pack, or a file
            // that appeared at the trace path before the open) still names its engine with a runId that no header
            // carries. The output claim and the command's prerequisites keep their precedence: they refuse before this.
            // Refused: logging it only on a run that opens its trace, which left a refusal naming no process at all.
            string runId = Guid.NewGuid().ToString();
            log.WriteLine(ParityRunLog.LoadedEngineLine(engine, runId));
            // N-parity-1107-03 C7 (signed revision H :45, :56-62, :230-232): a scenario/2 candidate, by the scan facts taken at
            // the single read (ScenarioFile), never by a parsed schema, which JsonDocument reads by its last member, so both
            // duplicate-schema orders and an escaped marker are candidates. It reaches the registry drift self-test here,
            // after the output claim and the command's prerequisites, before any registry consultation. Until C8 adopts a
            // table the self-test fails for every candidate: registry-integrity internal-defect, exit 2, no trace. A
            // candidate never falls through to the legacy reader below; with no admission route in this build, a verified
            // registry (impossible before C8) is the same named defect. Everything else takes today's route unchanged.
            if (scenarioFile.Scan != null && scenarioFile.Scan.Candidate)
            {
                if (!ParityPolicyRegistry.EnsureVerified(engine, runId, log)) return 2;
                log.WriteLine(ParityRunLog.RegistryIntegrityDefectLine("this build has no admission route for a verified registry", runId));
                return 2;
            }
            // N-bd-w11-shimeji-host-1-01: a path argument that names nothing (this scenario file, the pets folder in
            // LoadPets, the trace's folder below) is refused by name with exit 2, its line and no trace, as a refused map or
            // pack is. It is the caller's argument error, not scenario-invalid: there is no scenario to judge. Unchecked,
            // each threw FileNotFound or DirectoryNotFoundException out of the command, an unhandled exception's exit. The
            // read itself is ScenarioFile.Read's, before the claim; its fault is refused here, after the claim.
            if (scenarioFile.Fault != null)
            {
                log.WriteLine(ScenarioInputUnreadableLine("read", "refused " + scenarioPath + ": the scenario cannot be read: " + scenarioFile.Fault.Message));
                return 2;
            }
            byte[] scenarioBytes = scenarioFile.Bytes;
            // No whole-file strict UTF-8 decode before the parse (Codex's correction of round 5, 2026-11-13, b49851f2): it
            // was a new validator, and it refused no-candidate legacy files the old reader accepts, such as an invalid byte
            // inside a string the run never decodes (signed revision H :49). K5's detail names the operation that actually
            // failed: the file read is read, the parse below is syntax, a guarded string getter is schema-string or string.
            // A raw invalid byte does not by itself select read, and no decoder is added only to obtain that detail.
            // N-bd-w10-shimeji-host-1-01: a scenario that is not JSON, lacks a field or holds one the run cannot read is
            // scenario-invalid (CONTRACT sections 1 and 6), refused here with exit 2, its line and no trace, before the
            // pets and the pack are read. The oracle refuses the same scenario before it writes anything (oracle.py v1_run
            // calls the comparer's validate_scenario first), and 10.7 decides a scenario refused from the scenario alone
            // before either trace is read, so there is no run to abort. The refused alternative was an aborted run with a
            // complete footer: the header needs the id, rng and dpiScale such a scenario may lack, and a trace with no
            // header line is missing-field to the comparer. Read where the trace was already open and outside the run's
            // try, a missing field threw KeyNotFoundException out of the command and left an empty trace.
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(scenarioBytes);
            }
            catch (JsonException e)
            {
                log.WriteLine(ScenarioInputUnreadableLine("syntax", "refused " + scenarioPath + ": scenario-invalid: the scenario is not JSON: " + e.Message));
                return 2;
            }
            using (doc)
            {
                string refusal;
                Scenario scenario;
                try
                {
                    scenario = ReadScenario(doc.RootElement, out refusal);
                }
                catch (ScenarioInputUnreadableException e)
                {
                    // K5 schema-string: a schema value, rng.kind, start sid or start behavior ScenarioInvalid could not
                    // decode. Before, an InvalidOperationException out of the command.
                    log.WriteLine(ScenarioInputUnreadableLine("schema-string", "refused " + scenarioPath + ": the scenario's '" + e.Field
                        + "' string cannot be decoded: " + e.Message));
                    return 2;
                }
                if (scenario == null)
                {
                    log.WriteLine("refused " + scenarioPath + ": scenario-invalid: " + refusal);
                    return 2;
                }
                // Every map is parsed, compiled and applied before the trace file is opened, so a refused map leaves no
                // trace behind, not even an empty one a later step could take for a run.
                List<KeyValuePair<TickType, ParityMap>> pets = LoadPets(petsDir, log);
                if (pets == null) return 2;
                // The pack is hashed before the trace file is opened too, and for the same reason (N-bd-w6-coordinator-01).
                // Its preflight refuses a link or a pack that is not a folder (pack-invalid, CONTRACT section 3), and that
                // refusal comes before any run, as it does in the oracle and the comparer: there is no header to write (the
                // header IS the hash) and no run to abort, so it is exit 2 with its line and no trace. Hashed inside the
                // header write, after the trace was opened and before the run's try, it left an empty trace and escaped the
                // command as an exception. A footer-only trace was the refused alternative: with no header line the comparer
                // reads it as missing-field, a file a later step could take for a run.
                //
                // N-bd-w12-shimeji-host-1-02: a pack string no path can be made of (a NUL in it) is refused as the pack
                // argument it is, by name with exit 2, its line and no trace, as the run's own path arguments are; it threw
                // ArgumentException out of the command. Not scenario-invalid: CONTRACT section 1 has no rule on the pack's
                // characters, and the comparer's validate_scenario does not refuse it, so calling it that would be a
                // scenario rule only the host has. Named by the scenario that holds it, with the pack as JSON writes it,
                // since the value cannot be printed or resolved as it stands.
                string packDir, packSha256;
                try
                {
                    packDir = PackFolder(scenarioPath, scenario.Pack);
                }
                catch (Exception e) when (IsArgumentFault(e))
                {
                    log.WriteLine("refused " + scenarioPath + ": the pack " + JsonSerializer.Serialize(scenario.Pack) + " cannot be read: " + e.Message);
                    return 2;
                }
                try
                {
                    packSha256 = PackSha256(packDir);
                }
                catch (InvalidDataException e)
                {
                    log.WriteLine("refused " + packDir + ": " + e.Message);
                    return 2;
                }
                catch (Exception e) when (IsArgumentFault(e))
                {
                    // A pack folder whose contents cannot be read (a file another process holds open, a folder that denies
                    // listing) is the caller's argument fault too; unchecked, its IOException left the command unhandled.
                    log.WriteLine("refused " + packDir + ": the pack cannot be read: " + e.Message);
                    return 2;
                }
                // The trace is opened last, so every refusal above leaves none. One that cannot be opened (its folder does
                // not exist, the path is a folder, no access) is refused by name before anything is written; the folder is
                // not made for it (the rejected alternative): a mistyped folder would hold a trace no later step reads.
                // N-bd-w12-review-03: opened only as a NEW file (FileMode.CreateNew, never Create, which truncates). The
                // claim refused a path anything was already at, and a file that appeared there since (another run, the
                // caller) is refused here with the claim's own line, never truncated or replaced (TraceClaim says why).
                StreamWriter opened;
                try
                {
                    opened = new StreamWriter(new FileStream(tracePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                }
                catch (IOException e) when (IsAlreadyThere(e))
                {
                    log.WriteLine(AlreadyThere(tracePath, "a file"));
                    return 2;
                }
                catch (Exception e) when (IsArgumentFault(e))
                {
                    log.WriteLine("refused " + tracePath + ": the trace cannot be written: " + e.Message);
                    return 2;
                }
                using (StreamWriter trace = opened)
                {
                    trace.NewLine = "\n";
                    return Run(scenario, scenarioFile.Sha256, packSha256, pets, trace, build, log, traceOff, engine, synthetic, runId);
                }
            }
        }

        /// <summary>The scenario's schema (CONTRACT section 1).</summary>
        internal const string ScenarioSchema = "shimeji-parity-scenario/1";

        /// <summary>
        /// K5 (signed revision H step 3, :49; the name Codex agreed on 2026-11-13): the named producer-input diagnostic for
        /// a scenario whose bytes cannot be read (detail "read"), parsed (detail "syntax"), or whose eagerly decoded schema
        /// strings cannot be decoded (detail "schema-string"). Exit 2, no trace, no fabricated comparer grade. One constant,
        /// so an adopted respelling is a one-line change.
        /// </summary>
        internal const string ScenarioInputUnreadable = "scenario-input-unreadable";

        /// <summary>The K5 line: the token and its detail IN FRONT of the line the run wrote before, which follows byte for
        /// byte, so every existing reader of that line still finds it and a checker finds the token at the start.</summary>
        internal static string ScenarioInputUnreadableLine(string detail, string line)
        {
            return ScenarioInputUnreadable + " " + detail + ": " + line;
        }

        /// <summary>A scenario string that cannot be decoded (an escaped unpaired surrogate), thrown by
        /// <see cref="ScenarioText"/> out of <see cref="ScenarioInvalid"/> and refused at the run's ReadScenario call as the
        /// schema-string diagnostic.</summary>
        private sealed class ScenarioInputUnreadableException : Exception
        {
            public readonly string Field;

            public ScenarioInputUnreadableException(string field, InvalidOperationException fault) : base(fault.Message, fault)
            {
                Field = field;
            }
        }

        /// <summary>
        /// One eagerly decoded scenario string, its kind already checked as String, so the only fault left is the decode
        /// itself: JsonElement.GetString throws InvalidOperationException on an escaped unpaired surrogate. ScenarioInvalid
        /// runs before ReadScenario's refusal catch, so that fault left the command unhandled (the dispatcher in Program.cs
        /// has no outer catch). Guarded per site rather than by one catch round the method, so each site has its own check.
        /// </summary>
        private static string ScenarioText(JsonElement e, string field)
        {
            try
            {
                return e.GetString();
            }
            catch (InvalidOperationException fault)
            {
                throw new ScenarioInputUnreadableException(field, fault);
            }
        }

        /// <summary>The fields CONTRACT section 1 requires, as the comparer's validate_scenario lists them.</summary>
        private static readonly string[] ScenarioFields = { "id", "pack", "screens", "workArea", "dpiScale", "ticks", "rng", "settings", "mascots" };

        /// <summary>What a run reads from its scenario outside the run's try: all of it is read before the trace file is
        /// opened (<see cref="ReadScenario"/>), so nothing the scenario holds can fail between the header and the footer
        /// except inside the try, where a failure is the run's and is written as one.</summary>
        private sealed class Scenario
        {
            public JsonElement Root;
            public string Id, Pack, Rng;
            public double DpiScale;
            public int Ticks;
            public ParityEnvironment Environment;
            public ParityRandom Random;
            public ParitySettings Settings;
        }

        /// <summary>The scenario as the run reads it, or null with why it is scenario-invalid in <paramref name="refusal"/>.
        /// <see cref="ScenarioInvalid"/> names a missing or mistyped field; the readers then run here rather than after the
        /// trace is opened, and anything they still refuse (a screen with no "h", a tape value that is no number, a seed
        /// that is no hex) is scenario-invalid too, named by what was being read.</summary>
        private static Scenario ReadScenario(JsonElement s, out string refusal)
        {
            refusal = ScenarioInvalid(s);
            if (refusal != null) return null;
            string reading = "screens, workArea, activeWindow or input";
            try
            {
                var read = new Scenario { Root = s, Environment = new ParityEnvironment(s) };
                reading = "rng";
                read.Random = new ParityRandom(s.GetProperty("rng"));
                // Re-serialised compactly: the scenario's own text may be indented, and a trace is one object per line.
                read.Rng = JsonSerializer.Serialize(s.GetProperty("rng"));
                reading = "settings or typeSettings";
                read.Settings = new ParitySettings(s);
                reading = "dpiScale";
                read.DpiScale = s.GetProperty("dpiScale").GetDouble();
                read.Id = s.GetProperty("id").GetString();
                read.Pack = s.GetProperty("pack").GetString();
                read.Ticks = s.GetProperty("ticks").GetInt32();
                return read;
            }
            catch (Exception e) when (IsRefusal(e))
            {
                refusal = reading + " cannot be read: " + e.Message;
                return null;
            }
        }

        /// <summary>
        /// Why the scenario breaks CONTRACT section 1, or null. These are the comparer's validate_scenario rules, in its order
        /// and with its words, since the oracle applies that function before it writes anything: the schema, every required
        /// field, ticks a non-negative integer, rng.kind one of the three, one or more unique sids, a behavior for every start
        /// companion (a directed start runs no selection draw), and timelines in order inside 0..N with no tick-0 button
        /// edge. Then the kinds this run reads by name, which the comparer leaves to the producers: the id and pack strings,
        /// one or more screens, a tape's values and a splitmix64 seed, and each start companion's imageSet, anchor and
        /// lookRight.
        /// </summary>
        internal static string ScenarioInvalid(JsonElement s)
        {
            JsonElement e;
            if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("schema", out e) || e.ValueKind != JsonValueKind.String
                || ScenarioText(e, "schema") != ScenarioSchema)
                return "schema is not " + ScenarioSchema;
            foreach (string field in ScenarioFields)
                if (!s.TryGetProperty(field, out e)) return "lacks '" + field + "'";
            int ticks;
            if (!Integer(s.GetProperty("ticks"), out ticks) || ticks < 0) return "ticks is not a non-negative integer";
            JsonElement rng = s.GetProperty("rng");
            string kind = rng.ValueKind == JsonValueKind.Object && rng.TryGetProperty("kind", out e) && e.ValueKind == JsonValueKind.String ? ScenarioText(e, "rng.kind") : null;
            if (kind != "none" && kind != "tape" && kind != "splitmix64") return "rng.kind is not none, tape or splitmix64";
            JsonElement mascots = s.GetProperty("mascots");
            if (mascots.ValueKind != JsonValueKind.Array || mascots.GetArrayLength() == 0) return "mascots need one or more unique sids";
            var sids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement m in mascots.EnumerateArray())
            {
                string sidText = m.ValueKind == JsonValueKind.Object && m.TryGetProperty("sid", out e) && e.ValueKind == JsonValueKind.String
                    ? ScenarioText(e, "sid") : null;
                if (string.IsNullOrEmpty(sidText) || !sids.Add(sidText))
                    return "mascots need one or more unique sids";
            }
            foreach (JsonElement m in mascots.EnumerateArray())
                if (!m.TryGetProperty("behavior", out e) || e.ValueKind != JsonValueKind.String || ScenarioText(e, "behavior").Length == 0)
                    return "start companion '" + m.GetProperty("sid").GetString() + "' names no behavior (a directed start runs no selection draw)";
            foreach (string timeline in new[] { "input", "activeWindow" })
            {
                if (!s.TryGetProperty(timeline, out e)) continue;
                if (e.ValueKind != JsonValueKind.Array) return timeline + " is not a list of entries";
                int last = -1;
                foreach (JsonElement entry in e.EnumerateArray())
                {
                    JsonElement t;
                    int tick;
                    bool has = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("tick", out t);
                    if (!has || !Integer(entry.GetProperty("tick"), out tick) || tick < 0 || tick > ticks || tick < last)
                        return timeline + " entry tick " + (has ? entry.GetProperty("tick").GetRawText() : "(none)") + " is out of order or outside 0.." + ticks;
                    last = tick;
                    if (timeline == "input" && tick == 0
                        && (entry.TryGetProperty("press", out t) || entry.TryGetProperty("release", out t) || entry.TryGetProperty("click", out t)))
                        return "a tick-0 press, release or click (button edges start at tick 1)";
                }
            }
            if (s.GetProperty("id").ValueKind != JsonValueKind.String) return "id is not a string";
            if (s.GetProperty("pack").ValueKind != JsonValueKind.String) return "pack is not a string";
            // N-bd-w11-shimeji-host-1-02: the desktop is the bounding rectangle of the screens (ParityEnvironment), and an
            // empty list bounds nothing. Unrefused, the run went on over a rectangle built from Int32's limits, its width
            // wrapped to 1 in this unchecked build, and wrote a complete trace. The oracle's runner refuses the same
            // scenario (V1ScenarioRunner, V1_SCENARIO_INVALID no screens). CONTRACT section 1 and the comparer's
            // validate_scenario say nothing about an empty list; this follows the oracle.
            JsonElement screens = s.GetProperty("screens");
            if (screens.ValueKind != JsonValueKind.Array || screens.GetArrayLength() == 0) return "screens is not a list of one or more screens";
            if (kind == "tape" && (!rng.TryGetProperty("values", out e) || e.ValueKind != JsonValueKind.Array)) return "a tape rng lacks its values list";
            if (kind == "splitmix64" && (!rng.TryGetProperty("seed", out e) || e.ValueKind != JsonValueKind.String)) return "a splitmix64 rng lacks its seed string";
            foreach (JsonElement m in mascots.EnumerateArray())
            {
                string sid = "start companion '" + m.GetProperty("sid").GetString() + "'";
                int x;
                if (!m.TryGetProperty("imageSet", out e) || e.ValueKind != JsonValueKind.String) return sid + " lacks its imageSet string";
                if (!m.TryGetProperty("anchor", out e) || e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 2
                    || !Integer(e[0], out x) || !Integer(e[1], out x))
                    return sid + " lacks its anchor [x, y]";
                if (!m.TryGetProperty("lookRight", out e) || (e.ValueKind != JsonValueKind.True && e.ValueKind != JsonValueKind.False))
                    return sid + " lacks its lookRight boolean";
            }
            return null;
        }

        /// <summary>A JSON integer that fits Int32, as the run's readers take one (GetInt32 refuses 5.0 and 1e0).</summary>
        private static bool Integer(JsonElement e, out int value)
        {
            value = 0;
            return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value);
        }

        /// <summary>
        /// The end of a run a scenario-invalid edge aborted (Codex's closure ruling Q-e): the reconciliation's closing line,
        /// which on a run that stopped early is the incomplete count even when a first-failure line was logged before, then
        /// the footer, whose status a prior reconciliation failure keeps at error while the exit stays the refusal's 2.
        /// The abort path used to write "aborted" over that failure. Internal so ShimejiWindowSelfTest drives these same
        /// lines with a ledger it failed by hand, since no unmutated run fails its ledger.
        /// </summary>
        internal static int EndAborted(ParityTraceWriter writer, ParityRunLedger ledger, int ticks, ParityRandom random, TextWriter log)
        {
            string stopped = ledger.ClosingLine(ticks);
            if (stopped != null) log.WriteLine(stopped);
            Finish(writer, ticks, ledger.Failure != null ? "error" : "aborted", random, log);
            return 2;
        }

        private static int Run(Scenario scenario, string scenarioSha256, string packSha256, List<KeyValuePair<TickType, ParityMap>> pets,
            TextWriter trace, Dictionary<string, string> build, TextWriter log, bool traceOff, ParityLoadedEngine engine, bool synthetic, string runId)
        {
            {
                JsonElement s = scenario.Root;
                var maps = new Dictionary<string, ParityMap>(StringComparer.Ordinal);
                var types = new List<TickType>();
                foreach (KeyValuePair<TickType, ParityMap> pet in pets)
                {
                    maps[pet.Key.Name] = pet.Value;
                    types.Add(pet.Key);
                }

                ParityRandom random = scenario.Random;
                var writer = new ParityTraceWriter(trace, maps, traceOff);
                // N-parity-1107-03 (signed revision H :80, :150, :158): the invocation's one runId, made at the core run's
                // entry beside the loaded-engine line (Codex Q-c) and handed to the header, so every closure line this run
                // logs carries the same one. On a run below the command, the synthetic label follows the header, since it
                // qualifies the header's build values, which were its caller's (ParityRunLog holds the spellings).
                // C7: the digest taken at the single read (ScenarioFile.Sha256), of the same bytes, so the header is unchanged.
                writer.Header(scenario.Id, scenarioSha256, packSha256, build, scenario.Rng, scenario.DpiScale, runId);
                if (synthetic) log.WriteLine(ParityRunLog.SyntheticWitnessLine(runId));

                var world = new TickWorld(scenario.Environment, random, scenario.Settings, writer);
                foreach (TickType t in types) world.AddType(t);
                // policy:birth-cap's witness (H :158): every decision Breed's loop makes, in order, with the operands of the
                // comparison itself, logged once after the run with the world's limit write record. A parity run installs no
                // limit writer, so "limitWrites 0 none" is its no-override evidence; it is logged whatever the scenario
                // declares (policy-blind), and only birth-cap closure grades it.
                var decisions = new List<TickBirthDecision>();
                world.BirthDecided = decisions.Add;
                Action birthWitness = () => log.WriteLine(ParityRunLog.BirthWitnessLine(decisions, world.LimitWrites, world.MaximumCompanions, runId));
                // A15 (H :136, :142-150): the run's own ledger, fed the exact lines the writer commits, seeded from the
                // validated scenario's start rows before any companion is created (never from the world's lists or a
                // count of successful starts), and compared with the private lists at every checkpoint 0..N.
                var ledger = new ParityRunLedger(traceOff, log, runId);
                writer.Committed = ledger.Commit;
                foreach (JsonElement row in s.GetProperty("mascots").EnumerateArray()) ledger.Seed(row.GetProperty("sid").GetString());
                ParityEnvironment desk = scenario.Environment;
                int ticks = scenario.Ticks;
                string status = "complete";
                // CONTRACT v1.1 section 1: a left-button edge for a companion that is not active at dispatch is a scenario
                // error (the oracle refuses it the same way). Only this run sets the hook: the product drops such an edge.
                world.InactiveButtonTarget = input =>
                {
                    throw new ScenarioInvalidException("scenario-invalid: tick " + world.Step + ": left-button target " + input.Sid + " is not active");
                };
                // A14 (H :136): the window replay starts from the validated scenario's own timeline rows, never from the
                // environment's state, and is compared with the live desktop window at the same checkpoints. Seeded inside
                // the try (review of round 4), and each row's rectangle is read only when it takes effect, as the
                // environment reads it (ParityRunLedger.WindowTimeline): a rectangle neither validator checks fails the run
                // as it did at the base, exit 1 and an error footer, never an exception out of the command.
                try
                {
                    JsonElement timeline;
                    if (s.TryGetProperty("activeWindow", out timeline))
                        foreach (JsonElement entry in timeline.EnumerateArray()) ledger.WindowTimeline(entry);
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
                    ledger.StartupActivated();
                    writer.States(world);
                    ledger.Checkpoint(0, world.SnapshotMembership(), desk.DesktopWindow);
                    // A tape's end does not stop the loop (CONTRACT 10.5.1): a draw past it yields 0.5, the run steps on to
                    // tick N, and Finish logs the exhaustion and writes it as the footer's tapeExhausted.
                    for (int t = 1; t <= ticks; t++)
                    {
                        writer.Tick = t;
                        world.StepOnce();
                        writer.States(world);
                        ledger.Checkpoint(t, world.SnapshotMembership(), desk.DesktopWindow);
                    }
                    writer.FinalStates(world);
                    ledger.FinalStates(ticks, world.SnapshotMembership());
                }
                catch (ScenarioInvalidException e)
                {
                    // Nothing more is dispatched and the tick writes no state lines; the footer says aborted, and the exit
                    // is a refusal's, since the scenario, not the runtime, is at fault.
                    log.WriteLine(e.Message);
                    birthWitness();
                    return EndAborted(writer, ledger, ticks, random, log);
                }
                catch (Exception e)
                {
                    status = "error";
                    log.WriteLine("run failed: " + e);
                }
                birthWitness();
                // A15's verdict (H :136, :150): a failing checkpoint was logged when it failed and the run went on to N; its
                // status is error, set here before Finish, which stays as it was. Then the positive witness, or the count
                // of checkpoints a run that stopped early reached.
                if (ledger.Failure != null && status == "complete") status = "error";
                string closing = ledger.ClosingLine(ticks);
                if (closing != null) log.WriteLine(closing);
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
        /// before any file is read: only a link can lead a pack path outside the pack. A pack that is not a folder at all
        /// is pack-invalid too, as the comparer's preflight has it ("not a plain directory"): unchecked, the attribute read
        /// below threw FileNotFoundException, which no refusal catches.</summary>
        public static string PackSha256(string packDir)
        {
            return PackSha256(packDir, Directory.GetFiles);
        }

        /// <param name="listFiles">The files of one folder: Directory.GetFiles, except in ShimejiWindowSelfTest, which lists a
        /// second spelling beside a file as a case-sensitive folder would. A case-insensitive disk cannot hold both, and
        /// making a folder case-sensitive needs a right a self-test does not have.</param>
        internal static string PackSha256(string packDir, Func<string, string[]> listFiles)
        {
            if (!Directory.Exists(packDir)) throw new InvalidDataException("pack-invalid: the pack " + packDir + " is not a folder");
            if (IsLink(packDir)) throw new InvalidDataException("pack-invalid: the pack " + packDir + " is a link");
            var files = new List<string>();
            var folded = new Dictionary<string, string>(StringComparer.Ordinal);
            var dirs = new Stack<string>();
            dirs.Push(packDir);
            while (dirs.Count > 0)
            {
                string dir = dirs.Pop();
                foreach (string d in Directory.GetDirectories(dir))
                {
                    if (IsLink(d)) throw new InvalidDataException("pack-invalid: the pack holds a link: " + Relative(packDir, d));
                    RefuseCaseTwin(folded, Relative(packDir, d));
                    dirs.Push(d);
                }
                foreach (string f in listFiles(dir))
                {
                    if (IsLink(f)) throw new InvalidDataException("pack-invalid: the pack holds a link: " + Relative(packDir, f));
                    RefuseCaseTwin(folded, Relative(packDir, f));
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
        /// the code), or over the one file given (a script).
        ///
        /// N-bd-w12-shimeji-host-1-03: a folder with no *.dll is refused (InvalidDataException, by name with exit 2 in
        /// ParityRunCommand.Execute). Its manifest is empty, and the hash of an empty manifest is a well-formed 64-hex value
        /// that binds no converter code: every such folder, the wrong one included, would witness the same "converter".
        /// </summary>
        public static string ConverterSha256(string path)
        {
            string[] files = ConverterFiles(path);
            if (files.Length == 0)
                throw new InvalidDataException("the converter folder holds no *.dll, so its hash would bind no converter code");
            var rows = new List<KeyValuePair<string, string>>();
            foreach (string f in files) rows.Add(new KeyValuePair<string, string>(Path.GetFileName(f), Sha256(File.ReadAllBytes(f))));
            return ManifestSha256(rows);
        }

        /// <summary>The files converterSha256 binds: every *.dll in the folder given, or the one file given.</summary>
        private static string[] ConverterFiles(string path)
        {
            return Directory.Exists(path) ? Directory.GetFiles(path, "*.dll") : new[] { path };
        }

        private static string ManifestSha256(List<KeyValuePair<string, string>> rows)
        {
            // By UTF-8 bytes, as the comparer sorts; UTF-16 order differs for supplementary characters.
            rows.Sort((x, y) => CompareUtf8(x.Key, y.Key));
            var text = new StringBuilder();
            foreach (KeyValuePair<string, string> r in rows) text.Append(r.Value).Append("  ").Append(r.Key).Append('\n');
            return Sha256(Encoding.UTF8.GetBytes(text.ToString()));
        }

        /// <summary>
        /// N-bd-w10-shimeji-host-1-02, the comparer's rule (compare.py pack_preflight, review W123 F4): two entries that differ
        /// only in case (a pack built in a case-sensitive folder) are one file or folder to a case-insensitive copy, which
        /// keeps the first name and the last bytes, so the copied pack no longer matches its manifest. The comparer refuses
        /// such a pack as pack-invalid before it reads either trace; unrefused here, the host hashed it and wrote a trace
        /// for a pack no verdict can use. Both the upper and the lower form are keys, as the comparer tries upper() and
        /// casefold(). These are .NET's simple case mappings, which is how a case-insensitive disk compares names; Python's
        /// full mappings also refuse a few pairs a disk keeps apart (sharp s and "ss"), which the host hashes and the
        /// comparer still refuses, so no verdict differs.
        /// </summary>
        private static void RefuseCaseTwin(Dictionary<string, string> folded, string relative)
        {
            foreach (string key in new[] { relative.ToUpperInvariant(), relative.ToLowerInvariant() })
            {
                string other;
                if (!folded.TryGetValue(key, out other)) folded[key] = relative;
                else if (other != relative)
                    throw new InvalidDataException("pack-invalid: the pack holds entries that differ only in case: " + other + ", " + relative);
            }
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
