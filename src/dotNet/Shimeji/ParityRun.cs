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
    /// The converter's map from a converted pet back to the source pack (tools/ShimejiParity/CONTRACT.md section 9):
    /// frame indexes to source image keys, nodes and child references to refKeys. A test artefact: the product never
    /// reads one. Schema <c>shimeji-parity-map/1</c>:
    /// <code>{ "schema", "imageSet", "frames": [{ "frame", "image" }], "actions": { "&lt;node id&gt;": refKey },
    /// "children": { "&lt;node id&gt;:&lt;index&gt;": refKey }, "behaviours": { "&lt;node id&gt;": refKey } }</code>
    /// </summary>
    internal sealed class ParityMap
    {
        public const string Schema = "shimeji-parity-map/1";
        public string ImageSet;
        public readonly Dictionary<int, string> FrameImages = new Dictionary<int, string>();
        public readonly Dictionary<int, string> Actions = new Dictionary<int, string>();
        public readonly Dictionary<string, string> Children = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<int, string> Behaviours = new Dictionary<int, string>();
        /// <summary>CONTRACT v1.1 section 9 (decision 67, optional <c>"sourceNames": { "&lt;node id&gt;": name or null }</c>):
        /// the source's name of each behaviour or action node whose emitted name differs from it. A null value is a source
        /// element with no Name, "" an empty one; a node with no entry kept its name. Missing is never read as null.</summary>
        public readonly Dictionary<int, string> SourceNames = new Dictionary<int, string>();

        public static ParityMap Parse(string json)
        {
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                JsonElement root = doc.RootElement;
                if (Str(root, "schema") != Schema) throw new InvalidDataException("parity map schema is not " + Schema);
                var map = new ParityMap { ImageSet = Str(root, "imageSet") };
                JsonElement e;
                if (root.TryGetProperty("frames", out e))
                    foreach (JsonElement f in e.EnumerateArray())
                    {
                        map.FrameImages[f.GetProperty("frame").GetInt32()] = Str(f, "image");
                        map.ReadGeometry(f);
                    }
                if (root.TryGetProperty("actions", out e))
                    foreach (JsonProperty p in e.EnumerateObject()) map.Actions[int.Parse(p.Name, CultureInfo.InvariantCulture)] = p.Value.GetString();
                if (root.TryGetProperty("children", out e))
                    foreach (JsonProperty p in e.EnumerateObject()) map.Children[p.Name] = p.Value.GetString();
                if (root.TryGetProperty("behaviours", out e))
                    foreach (JsonProperty p in e.EnumerateObject()) map.Behaviours[int.Parse(p.Name, CultureInfo.InvariantCulture)] = p.Value.GetString();
                if (root.TryGetProperty("sourceNames", out e))
                    foreach (JsonProperty p in e.EnumerateObject())
                    {
                        if (p.Value.ValueKind != JsonValueKind.Null && p.Value.ValueKind != JsonValueKind.String)
                            throw new InvalidDataException("parity map sourceNames entry " + p.Name + " is neither a name nor null");
                        map.SourceNames[int.Parse(p.Name, CultureInfo.InvariantCulture)] = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString();
                    }
                return map;
            }
        }

        /// <summary>Give every node and child reference its refKey; a node the map does not name is a hole in the
        /// converter's map, refused rather than traced with a made-up key.</summary>
        public void Apply(TickType type)
        {
            string key;
            foreach (TickActDef act in type.Actions.Values)
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
            CheckGeometry(type);
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
            foreach (TickActDef act in type.Actions.Values)
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

    /// <summary>CONTRACT section 2: none (any draw fails the run), a checked tape, or splitmix64.</summary>
    internal sealed class ParityRandom : ITickRandom
    {
        private readonly string _kind;
        private readonly List<double> _tape = new List<double>();
        private ulong _state;
        private int _next;

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

        /// <summary>Set when a draw was impossible (a directed run drew, or the tape ran out); the run then ends with
        /// status error and the comparer reports the class.</summary>
        public string Exhausted;

        public double Next()
        {
            switch (_kind)
            {
                case "tape":
                    if (_next >= _tape.Count) { Exhausted = "rng-tape-exhausted"; return 0.5; }
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
        private readonly TextWriter _out;
        private readonly Dictionary<string, ParityMap> _maps;
        private int _tick;
        private int _states, _events, _draws;
        private readonly Dictionary<string, TickPoint> _lastAnchor = new Dictionary<string, TickPoint>(StringComparer.Ordinal);

        public ParityTraceWriter(TextWriter output, Dictionary<string, ParityMap> maps)
        {
            _out = output;
            _maps = maps;
        }

        public int Tick { set { _tick = value; } }

        public void Header(string scenarioId, string scenarioSha, string packSha, Dictionary<string, string> build, string rngJson, double dpiScale)
        {
            var w = new JsonLine();
            w.Str("type", "header").Str("schema", "shimeji-parity-trace/1").Str("runtime", "host");
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
            _out.WriteLine(w.End());
        }

        public void States(TickWorld world)
        {
            foreach (TickMascot m in world.Mascots) State(m, false);
            foreach (TickMascot m in world.Pending) State(m, true);
        }

        private void State(TickMascot m, bool pending)
        {
            // The image belongs to the type whose pose set it, which after a Transform is still the old one.
            ParityMap map;
            _maps.TryGetValue((m.PoseType ?? m.Type).Name, out map);
            TickBehaviour beh = m.Behaviour;
            TickAction leaf = beh == null ? null : beh.Action.Leaf;
            List<string> stack = beh == null ? new List<string>() : beh.ActionStack();
            TickPoint prev;
            TickPoint velocity = _lastAnchor.TryGetValue(m.Sid, out prev) ? new TickPoint(m.Anchor.X - prev.X, m.Anchor.Y - prev.Y) : new TickPoint(0, 0);
            _lastAnchor[m.Sid] = m.Anchor;
            var w = new JsonLine();
            w.Str("type", "state").Num("tick", _tick).Str("sid", m.Sid).Str("imageSet", m.Type.Name)
                .Str("behavior", BehaviourName(beh)).Str("action", leaf == null ? null : NodeName(beh.Type, leaf.Def.NodeId, leaf.Name))
                .Str("actionClass", leaf == null ? null : (leaf.Def.Type == "Embedded" ? leaf.Def.Class : leaf.Def.Type))
                .StrList("actionStack", stack).Str("refKey", stack.Count > 0 ? stack[stack.Count - 1] : null)
                .Num("actionAge", leaf == null ? (double?)null : leaf.Time);
            if (m.Pose == null) w.Raw("pose", "null").Raw("poseAnchor", "null");
            else
            {
                int index = Array.IndexOf(m.PoseVariant.Poses, m.Pose);
                w.Raw("pose", "{\"image\":" + JsonLine.Quote(map == null ? null : map.ImageOf(m.Pose.Frame))
                    + ",\"imageRight\":" + JsonLine.Quote(map == null ? null : map.ImageOf(m.Pose.Right))
                    + ",\"index\":" + index + "}");
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
            _out.WriteLine(w.End());
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

        public void Footer(int ticks, string status)
        {
            _out.WriteLine(new JsonLine().Str("type", "footer").Num("ticks", ticks).Str("status", status)
                .Num("draws", _draws).Num("states", _states).Num("events", _events).End());
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

        /// <summary><c>--shimeji-parity-run &lt;scenario.json&gt; &lt;pets dir&gt; &lt;trace out&gt; &lt;converter exe&gt;</c>.
        /// Exit 0 = a complete trace, 1 = the run ended in error (the trace says why), 2 = refused.</summary>
        public static int Execute(string[] args, int at, TextWriter log)
        {
            if (args.Length < at + 5)
            {
                log.WriteLine("usage: " + Flag + " <scenario.json> <pets dir> <trace out> <converter exe>");
                return 2;
            }
            if (!AppPaths.IsDataRootOverridden)
            {
                log.WriteLine(Flag + " runs only under an overridden test data root (" + AppPaths.DataRootOverrideEnvironmentVariable + ").");
                return 2;
            }
            var build = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "commit", typeof(ParityRun).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false) is System.Reflection.AssemblyInformationalVersionAttribute[] v && v.Length > 0 ? v[0].InformationalVersion : "unknown" },
                { "exeSha256", ParityRun.Sha256(File.ReadAllBytes(Environment.ProcessPath)) },
                { "engineSha256", ParityRun.Sha256(File.ReadAllBytes(typeof(TickWorld).Assembly.Location)) },
                { "converterSha256", ParityRun.ConverterSha256(args[at + 4]) },
            };
            return ParityRun.Run(args[at + 1], args[at + 2], args[at + 3], build, log);
        }
    }

    /// <summary>
    /// <c>--shimeji-parity-run &lt;scenario.json&gt; &lt;pets dir&gt; &lt;trace out&gt;</c>: run one scenario headless, with
    /// no window, no real input and no real environment, and write the host's trace. The pets directory holds one
    /// folder per image set with its converted <c>animations.xml</c> and <c>parity-map.json</c>.
    /// </summary>
    internal static class ParityRun
    {
        public static int Run(string scenarioPath, string petsDir, string tracePath, Dictionary<string, string> build, TextWriter log)
        {
            byte[] scenarioBytes = File.ReadAllBytes(scenarioPath);
            using (JsonDocument doc = JsonDocument.Parse(scenarioBytes))
            using (var trace = new StreamWriter(tracePath, false, new UTF8Encoding(false)))
            {
                JsonElement s = doc.RootElement;
                trace.NewLine = "\n";
                var maps = new Dictionary<string, ParityMap>(StringComparer.Ordinal);
                var types = new List<TickType>();
                foreach (string dir in Directory.GetDirectories(petsDir))
                {
                    string xmlPath = Path.Combine(dir, "animations.xml"), mapPath = Path.Combine(dir, "parity-map.json");
                    if (!File.Exists(xmlPath) || !File.Exists(mapPath)) continue;
                    XmlData.RootNode root;
                    string error;
                    if (!CompanionXmlValidator.TryParseWithFeatures(File.ReadAllText(xmlPath), CompanionFormatFeatures.All, out root, out error))
                    {
                        log.WriteLine("refused " + xmlPath + ": " + error);
                        return 2;
                    }
                    ParityMap map = ParityMap.Parse(File.ReadAllText(mapPath));
                    TickType type = TickType.Compile(map.ImageSet, root);
                    map.Apply(type);
                    maps[type.Name] = map;
                    types.Add(type);
                }

                var env = new ParityEnvironment(s);
                var random = new ParityRandom(s.GetProperty("rng"));
                var settings = new ParitySettings(s);
                var writer = new ParityTraceWriter(trace, maps);
                string packDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scenarioPath)), s.GetProperty("pack").GetString()));
                writer.Header(s.GetProperty("id").GetString(), Sha256(scenarioBytes), PackSha256(packDir), build,
                    // Re-serialised compactly: the scenario's own text may be indented, and a trace is one object per line.
                    JsonSerializer.Serialize(s.GetProperty("rng")), s.GetProperty("dpiScale").GetDouble());

                var world = new TickWorld(env, random, settings, writer);
                foreach (TickType t in types) world.AddType(t);
                int ticks = s.GetProperty("ticks").GetInt32();
                string status = "complete";
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
                    for (int t = 1; t <= ticks; t++)
                    {
                        writer.Tick = t;
                        world.StepOnce();
                        writer.States(world);
                        if (random.Exhausted != null) { status = "error"; log.WriteLine(random.Exhausted); break; }
                    }
                }
                catch (Exception e)
                {
                    status = "error";
                    log.WriteLine("run failed: " + e);
                }
                if (writer.ProvenanceFault != null)
                {
                    status = "error";
                    log.WriteLine("parity trace: " + writer.ProvenanceFault);
                }
                writer.Footer(ticks, status);
                return status == "complete" ? 0 : 1;
            }
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
