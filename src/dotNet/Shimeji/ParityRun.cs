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
                        map.FrameImages[f.GetProperty("frame").GetInt32()] = Str(f, "image");
                if (root.TryGetProperty("actions", out e))
                    foreach (JsonProperty p in e.EnumerateObject()) map.Actions[int.Parse(p.Name, CultureInfo.InvariantCulture)] = p.Value.GetString();
                if (root.TryGetProperty("children", out e))
                    foreach (JsonProperty p in e.EnumerateObject()) map.Children[p.Name] = p.Value.GetString();
                if (root.TryGetProperty("behaviours", out e))
                    foreach (JsonProperty p in e.EnumerateObject()) map.Behaviours[int.Parse(p.Name, CultureInfo.InvariantCulture)] = p.Value.GetString();
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
        private int _cursorX, _cursorY, _cursorDx, _cursorDy;
        private TickPoint _target;

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
            _cursorDx = (_cursorDx + _target.X - _cursorX) / 2;
            _cursorDy = (_cursorDy + _target.Y - _cursorY) / 2;
            _cursorX = _target.X;
            _cursorY = _target.Y;
            _workAreaArea.Set(_workArea[0], _workArea[1], _workArea[2], _workArea[3]);
            JsonElement window = default(JsonElement);
            bool any = false;
            foreach (KeyValuePair<int, JsonElement> entry in _window)
                if (entry.Key <= tick) { window = entry.Value; any = true; }
            JsonElement visible, rect;
            bool shown = any && window.TryGetProperty("visible", out visible) && visible.ValueKind == JsonValueKind.True
                && window.TryGetProperty("rect", out rect);
            _activeWindow.Visible = shown;
            if (shown)
            {
                rect = window.GetProperty("rect");
                int left = rect[0].GetInt32(), top = rect[1].GetInt32();
                _activeWindow.Set(left, top, rect[2].GetInt32() - left, rect[3].GetInt32() - top);
            }
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
            Dictionary<string, bool> d;
            bool v;
            if (imageSet != null && _perType.TryGetValue(imageSet, out d) && d.TryGetValue(setting, out v)) return v;
            if (_global.TryGetValue(setting, out v)) return v;
            // The source's defaults: everything on except Multiscreen, which a scenario always states.
            return !string.Equals(setting, "Multiscreen", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsBehaviourEnabled(TickBehaviourDef behaviour, string imageSet)
        {
            if (string.IsNullOrEmpty(behaviour.Toggle)) return true;
            return Get("behaviour:" + behaviour.Toggle, imageSet);
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
                .Str("behavior", beh == null ? null : beh.Name).Str("action", leaf == null ? null : leaf.Name)
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
                TickPoint c = m.ImageCenter;
                w.Raw("poseAnchor", "[" + c.X + "," + c.Y + "]");
            }
            w.Bool("visible", m.Visible).Raw("anchor", "[" + m.Anchor.X + "," + m.Anchor.Y + "]");
            if (m.Pose == null) w.Raw("origin", "null");
            else
            {
                TickPoint c = m.ImageCenter;
                w.Raw("origin", "[" + (m.Anchor.X - c.X) + "," + (m.Anchor.Y - c.Y) + "]");
            }
            w.Bool("lookRight", m.LookRight).Raw("velocity", "[" + velocity.X + "," + velocity.Y + "]")
                .StrList("affordances", m.Affordances).Raw("target", "null").Bool("dragging", m.Dragging)
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

        public void BehaviourEntered(TickMascot mascot, string from, string to, string cause)
        {
            _out.WriteLine(Event(mascot, "behavior").Str("from", from).Str("to", to).Str("cause", cause).End());
        }

        public void BirthRequested(TickMascot parent, TickMascot child, int ordinal, int attempt)
        {
            _out.WriteLine(Event(parent, "birth-request").Str("child", child.Sid).Str("imageSet", child.Type.Name)
                .Str("behavior", child.Behaviour == null ? null : child.Behaviour.Name)
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
            _out.WriteLine(Event(mascot, "transform").Str("fromImageSet", fromImageSet).Str("toImageSet", toImageSet).Str("behavior", behaviour).End());
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
                { "converterSha256", ParityRun.Sha256(File.ReadAllBytes(args[at + 4])) },
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

        /// <summary>CONTRACT section 3: '&lt;sha256&gt;  &lt;relative path&gt;\n' per file, sorted by path in ordinal order.</summary>
        public static string PackSha256(string packDir)
        {
            var rows = new List<KeyValuePair<string, string>>();
            foreach (string file in Directory.GetFiles(packDir, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(packDir.Length).TrimStart('\\', '/').Replace('\\', '/');
                rows.Add(new KeyValuePair<string, string>(rel, Sha256(File.ReadAllBytes(file))));
            }
            // Ordinal by UTF-8 bytes, as the comparer sorts; UTF-16 order differs for some non-ASCII paths.
            rows.Sort((x, y) => CompareUtf8(x.Key, y.Key));
            var text = new StringBuilder();
            foreach (KeyValuePair<string, string> r in rows) text.Append(r.Value).Append("  ").Append(r.Key).Append('\n');
            return Sha256(Encoding.UTF8.GetBytes(text.ToString()));
        }
    }
}
