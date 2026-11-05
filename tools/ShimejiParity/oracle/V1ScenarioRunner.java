package com.group_finity.mascot.parity;

import com.group_finity.mascot.Main;
import com.group_finity.mascot.Manager;
import com.group_finity.mascot.Mascot;
import com.group_finity.mascot.NativeFactory;
import com.group_finity.mascot.Platform;
import com.group_finity.mascot.action.ActionBase;
import com.group_finity.mascot.action.ComplexAction;
import com.group_finity.mascot.animation.Pose;
import com.group_finity.mascot.behavior.Behavior;
import com.group_finity.mascot.config.Configuration;
import com.group_finity.mascot.config.Entry;
import com.group_finity.mascot.environment.Area;
import com.group_finity.mascot.environment.Environment;
import com.group_finity.mascot.exception.OutOfMemoryException;
import com.group_finity.mascot.image.MascotImage;
import com.group_finity.mascot.image.NativeImage;
import com.group_finity.mascot.image.TranslucentWindow;
import com.group_finity.mascot.script.Constant;
import com.group_finity.mascot.script.Variable;
import com.group_finity.mascot.script.VariableMap;
import com.joconner.i18n.Utf8ResourceBundleControl;
import java.awt.Component;
import java.awt.Point;
import java.awt.Rectangle;
import java.awt.Window;
import java.awt.event.InputEvent;
import java.awt.event.MouseEvent;
import java.awt.event.MouseListener;
import java.awt.event.MouseMotionListener;
import java.awt.image.BufferedImage;
import java.io.File;
import java.io.FileOutputStream;
import java.io.OutputStreamWriter;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.Collection;
import java.util.IdentityHashMap;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.ResourceBundle;
import java.util.Set;
import javax.script.ScriptEngine;
import javax.script.ScriptEngineManager;
import javax.swing.JPanel;
import javax.xml.parsers.DocumentBuilderFactory;
import org.w3c.dom.Document;
import org.w3c.dom.Element;
import org.w3c.dom.Node;

/**
 * CONTRACT v1 scenario runner over the patched real runtime (tools/ShimejiParity/CONTRACT.md sections 1-5).
 *
 * The controller is installed before any Environment, NativeFactory or Mascot class initialises, so the tick-0
 * scenario snapshot is the first desktop the runtime ever sees. The runtime's own Manager.tick runs once per scenario
 * step through the parityStep seam; its real timer thread is never started. The native factory is this runner's own:
 * an environment fed only by the scenario, translucent windows that are plain never-displayed panels, and image
 * handles with no native resources. Nothing reads the real cursor, windows or displays, and no window is ever made
 * displayable (checked at the end of every run).
 *
 * Observation never evaluates anything. Names, keys and ages come from stored fields and from the seam callbacks
 * the source itself makes while it builds and steps (edit-spec.json): the builder hooks bind each runtime object to
 * the DOM element it was built from, the behaviour hook runs before the new behaviour initialises, and the pose hook
 * records the pose the source actually applied. Rejected alternative: re-deriving keys by matching names, which cannot
 * tell two references to one action apart (CONTRACT section 3, refKey).
 *
 * Arguments: header.json scenario.json trace.jsonl [off]. "off" keeps every control (environment, RNG, input) and
 * writes only the final tick's state lines, the trace-on/trace-off proof of CONTRACT section 5.
 */
public final class V1ScenarioRunner implements DraftParityHooks.Controller {
    private static final String SCHEMA = "shimeji-parity-scenario/1";
    /** Embedded classes whose CONTRACT section 3 events (scan-target, interact, sound, mute) are not observed yet. */
    private static final Set<String> UNOBSERVED = new LinkedHashSet<String>(java.util.Arrays.asList(
        "ScanMove", "ScanInteract", "ScanJump", "Interact", "Mute", "Broadcast", "BroadcastMove", "BroadcastJump",
        "BroadcastStay", "ComplexMove", "ComplexJump"));

    private final Map<String, Object> scenario;
    private final boolean observing;
    private final DraftTraceWriter writer;
    private final int ticks;
    private final Map<String, Object> rng;
    private final List<Double> tape = new ArrayList<Double>();
    private long splitmix;
    private int tapeCursor;
    private boolean tapeExhausted;

    private int tick;
    private boolean startup = true;
    private boolean stepInputPending;
    private int states;
    private int eventCount;
    private int draws;
    private final List<Map<String, Object>> pending = new ArrayList<Map<String, Object>>();

    // Identity: every sid the trace uses, and the lineage counters of CONTRACT section 3.
    private final IdentityHashMap<Mascot, String> sids = new IdentityHashMap<Mascot, String>();
    private final IdentityHashMap<Mascot, Boolean> tentative = new IdentityHashMap<Mascot, Boolean>();
    private final IdentityHashMap<Mascot, Integer> ordinals = new IdentityHashMap<Mascot, Integer>();
    private final IdentityHashMap<Mascot, Integer> attempts = new IdentityHashMap<Mascot, Integer>();
    private final Map<String, Point> lastAnchor = new LinkedHashMap<String, Point>();
    private final IdentityHashMap<Mascot, String> lastBehaviour = new IdentityHashMap<Mascot, String>();
    private final IdentityHashMap<Mascot, Pose> appliedPose = new IdentityHashMap<Mascot, Pose>();
    private final IdentityHashMap<Mascot, Boolean> disposing = new IdentityHashMap<Mascot, Boolean>();
    private final IdentityHashMap<Mascot, String[]> pendingTransform = new IdentityHashMap<Mascot, String[]>();
    private Mascot context;

    // Build-time metadata from the builder hooks.
    private final IdentityHashMap<Object, Element> elements = new IdentityHashMap<Object, Element>();
    private final IdentityHashMap<Object, String> builderSets = new IdentityHashMap<Object, String>();
    private final IdentityHashMap<Object, Boolean> rootBehaviours = new IdentityHashMap<Object, Boolean>();
    private final IdentityHashMap<VariableMap, Object> actionOrigin = new IdentityHashMap<VariableMap, Object>();
    private final IdentityHashMap<VariableMap, Object> actionDeclaration = new IdentityHashMap<VariableMap, Object>();
    private final IdentityHashMap<Object, Object[]> behaviourOrigin = new IdentityHashMap<Object, Object[]>();
    private final IdentityHashMap<Object, Long> entered = new IdentityHashMap<Object, Long>();
    private final IdentityHashMap<Pose, Integer> poseIndex = new IdentityHashMap<Pose, Integer>();
    private final IdentityHashMap<Document, String> documents = new IdentityHashMap<Document, String>();
    private final List<Object[]> refStack = new ArrayList<Object[]>();
    private Object pendingBuilder;
    private String pendingCause;
    private boolean fallbackPending;
    private String namedBuild;
    private long enterSequence;

    // Scenario desktop.
    private final Rectangle screenBounds;
    private final Map<String, Rectangle> screenMap = new LinkedHashMap<String, Rectangle>();
    private final Rectangle workAreaRect;
    private int appliedWindowEntry = -1;
    private Mascot held;

    private V1ScenarioRunner(Map<String, Object> scenario, DraftTraceWriter writer, boolean observing) {
        this.scenario = scenario;
        this.writer = writer;
        this.observing = observing;
        this.ticks = integer(scenario.get("ticks"));
        this.rng = object(scenario.get("rng"));
        String kind = (String)rng.get("kind");
        if ("tape".equals(kind)) for (Object value : array(rng.get("values"))) tape.add(((Number)value).doubleValue());
        else if ("splitmix64".equals(kind)) {
            String seed = (String)rng.get("seed");
            splitmix = Long.parseUnsignedLong(seed.startsWith("0x") || seed.startsWith("0X") ? seed.substring(2) : seed, 16);
        } else if (!"none".equals(kind)) throw new IllegalStateException("V1_SCENARIO_INVALID rng.kind " + kind);
        Rectangle bounds = null;
        int index = 0;
        for (Object entry : array(scenario.get("screens"))) {
            Rectangle rect = rectangle(entry);
            screenMap.put("scenario-screen-" + index++, rect);
            bounds = bounds == null ? rect : bounds.union(rect);
        }
        if (bounds == null) throw new IllegalStateException("V1_SCENARIO_INVALID no screens");
        screenBounds = bounds;
        workAreaRect = rectangle(scenario.get("workArea"));
    }

    // ------------------------------------------------------------------ JSON and reflection helpers

    private static int integer(Object value) { return ((Number)value).intValue(); }

    @SuppressWarnings("unchecked")
    private static Map<String, Object> object(Object value) { return (Map<String, Object>)value; }

    @SuppressWarnings("unchecked")
    private static List<Object> array(Object value) { return value == null ? new ArrayList<Object>() : (List<Object>)value; }

    private static Rectangle rectangle(Object value) {
        Map<String, Object> item = object(value);
        return new Rectangle(integer(item.get("x")), integer(item.get("y")), integer(item.get("w")), integer(item.get("h")));
    }

    private static Point point(Object value) {
        List<Object> pair = array(value);
        return new Point(integer(pair.get(0)), integer(pair.get(1)));
    }

    private static Field field(Class<?> type, String name) throws Exception {
        for (Class<?> current = type; current != null; current = current.getSuperclass()) {
            try {
                Field item = current.getDeclaredField(name);
                item.setAccessible(true);
                return item;
            } catch (NoSuchFieldException ignored) {
                // keep looking in the superclass
            }
        }
        throw new NoSuchFieldException(type.getName() + "." + name);
    }

    private static Object read(Object owner, String name) {
        try { return field(owner.getClass(), name).get(owner); }
        catch (Exception failure) { throw new IllegalStateException("V1_OBSERVE_FIELD " + name, failure); }
    }

    private static List<Integer> pair(int x, int y) {
        List<Integer> result = new ArrayList<Integer>();
        result.add(x);
        result.add(y);
        return result;
    }

    private static List<Integer> ltrb(Rectangle rect) {
        List<Integer> result = pair(rect.x, rect.y);
        result.add(rect.x + rect.width);
        result.add(rect.y + rect.height);
        return result;
    }

    // ------------------------------------------------------------------ events and states

    private Map<String, Object> event(String sid, String kind) {
        Map<String, Object> record = new LinkedHashMap<String, Object>();
        record.put("type", "event");
        record.put("tick", tick);
        record.put("sid", sid);
        record.put("kind", kind);
        return record;
    }

    private void emit(Map<String, Object> record) {
        eventCount++;
        if (observing) pending.add(record);
    }

    private void flush() throws Exception {
        for (Map<String, Object> record : pending) writer.writeRecord(record);
        pending.clear();
    }

    private String sid(Mascot mascot) {
        if (mascot == null) return null;
        String value = sids.get(mascot);
        if (value == null) throw new IllegalStateException("V1_UNKNOWN_MASCOT " + mascot);
        return value;
    }

    private Mascot actor() {
        Mascot current = DraftParityHooks.currentMascot();
        return current != null ? current : context;
    }

    private String contextSid() { return actor() == null ? null : sids.get(actor()); }

    @SuppressWarnings("unchecked")
    private List<Mascot> active() { return new ArrayList<Mascot>((List<Mascot>)read(manager(), "mascots")); }

    @SuppressWarnings("unchecked")
    private List<Mascot> queued() { return new ArrayList<Mascot>((Set<Mascot>)read(manager(), "added")); }

    private static Manager manager() { return (Manager)read(Main.getInstance(), "manager"); }

    private boolean managed(Mascot mascot) { return active().contains(mascot) || queued().contains(mascot); }

    private void writeStates() throws Exception {
        for (Mascot mascot : active()) state(mascot, false);
        for (Mascot mascot : queued()) state(mascot, true);
    }

    private void state(Mascot mascot, boolean isPending) throws Exception {
        String sid = sid(mascot);
        Map<String, Object> record = new LinkedHashMap<String, Object>();
        Behavior behaviour = mascot.getBehavior();
        List<String> stack = new ArrayList<String>();
        Object leaf = null;
        if (behaviour != null) {
            stack.add(behaviourKey(behaviour));
            Object action = read(behaviour, "action");
            stack.add(actionKey(action));
            leaf = action;
            while (leaf instanceof ComplexAction) {
                Object[] children = (Object[])read(leaf, "actions");
                int current = (Integer)read(leaf, "currentAction");
                if (current >= children.length || !enteredAfter(children[current], leaf)) break;
                leaf = children[current];
                stack.add(actionKey(leaf));
            }
        }
        boolean leafEntered = leaf != null && read(leaf, "mascot") != null;
        Pose pose = appliedPose.get(mascot);
        MascotImage image = mascot.getImage();
        boolean isDisposing = disposing.containsKey(mascot);
        record.put("type", "state");
        record.put("tick", tick);
        record.put("sid", sid);
        record.put("imageSet", mascot.getImageSet());
        record.put("behavior", behaviour == null ? null : (String)read(behaviour, "name"));
        record.put("action", leaf == null ? null : actionName(leaf));
        record.put("actionClass", leaf == null ? null : leaf.getClass().getSimpleName());
        record.put("actionStack", stack);
        record.put("refKey", stack.isEmpty() ? null : stack.get(stack.size() - 1));
        record.put("actionAge", leafEntered ? Integer.valueOf(mascot.getTime() - (Integer)read(leaf, "startTime")) : null);
        if (pose == null) {
            record.put("pose", null);
            record.put("poseAnchor", null);
        } else {
            Map<String, Object> applied = new LinkedHashMap<String, Object>();
            applied.put("image", packPath((Path)read(pose, "image")));
            applied.put("imageRight", packPath((Path)read(pose, "rightImage")));
            Integer index = poseIndex.get(pose);
            if (index == null) throw new IllegalStateException("V1_POSE_UNREGISTERED " + sid);
            applied.put("index", index);
            record.put("pose", applied);
            record.put("poseAnchor", image == null ? null : pair(image.getCenter().x, image.getCenter().y));
        }
        record.put("visible", pose != null && image != null && !isDisposing);
        Point anchor = mascot.getAnchor();
        record.put("anchor", pair(anchor.x, anchor.y));
        if (pose == null) record.put("origin", null);
        else {
            Rectangle bounds = mascot.getBounds();
            record.put("origin", pair(bounds.x, bounds.y));
        }
        record.put("lookRight", mascot.isLookRight());
        Point previous = lastAnchor.get(sid);
        record.put("velocity", previous == null ? pair(0, 0) : pair(anchor.x - previous.x, anchor.y - previous.y));
        lastAnchor.put(sid, new Point(anchor));
        record.put("affordances", new ArrayList<String>(mascot.getAffordances()));
        record.put("target", null);
        record.put("dragging", mascot.isDragging());
        record.put("pending", isPending);
        record.put("disposing", isDisposing);
        record.put("vars", storedVariables(mascot));
        states++;
        writer.writeRecord(record);
    }

    /** Stored scalar variables only: a Constant's stored value, never an evaluation (CONTRACT section 5). */
    private Map<String, Object> storedVariables(Mascot mascot) {
        Map<String, Object> result = new LinkedHashMap<String, Object>();
        VariableMap variables = (VariableMap)read(mascot, "variables");
        if (variables == null) return result;
        List<String> names = new ArrayList<String>(variables.getRawMap().keySet());
        java.util.Collections.sort(names);
        for (String name : names) {
            Variable variable = variables.getRawMap().get(name);
            if (!(variable instanceof Constant)) continue;
            Object value = read(variable, "value");
            if (value instanceof Number || value instanceof Boolean || value instanceof String) result.put(name, value);
        }
        return result;
    }

    private boolean enteredAfter(Object child, Object parent) {
        Long childEntry = entered.get(child);
        Long parentEntry = entered.get(parent);
        return childEntry != null && parentEntry != null && childEntry > parentEntry;
    }

    private String actionName(Object action) {
        Object declaration = actionDeclaration.get((VariableMap)read(action, "variables"));
        if (declaration == null) throw new IllegalStateException("V1_ACTION_UNBOUND " + action.getClass().getSimpleName());
        return (String)read(declaration, "name");
    }

    private String actionKey(Object action) {
        Object origin = actionOrigin.get((VariableMap)read(action, "variables"));
        if (origin == null) throw new IllegalStateException("V1_ACTION_UNBOUND " + action.getClass().getSimpleName());
        return key(origin);
    }

    private String behaviourKey(Object behaviour) {
        Object[] origin = behaviourOrigin.get(behaviour);
        if (origin == null) throw new IllegalStateException("V1_BEHAVIOUR_UNBOUND " + behaviour);
        return key(origin[0]);
    }

    /** refKey: imageSet | pack-relative document path | element path of LocalName[i] steps (CONTRACT section 3). */
    private String key(Object builder) {
        Element element = elements.get(builder);
        String set = builderSets.get(builder);
        if (element == null || set == null) throw new IllegalStateException("V1_REFKEY_UNBOUND " + builder);
        StringBuilder path = new StringBuilder();
        for (Node node = element; node != null && node.getNodeType() == Node.ELEMENT_NODE; node = node.getParentNode()) {
            String local = localName(node);
            int index = 1;
            for (Node sibling = node.getPreviousSibling(); sibling != null; sibling = sibling.getPreviousSibling()) {
                if (sibling.getNodeType() == Node.ELEMENT_NODE && localName(sibling).equals(local)) index++;
            }
            path.insert(0, "/" + local + "[" + index + "]");
        }
        return set + "|" + documentPath(element.getOwnerDocument()) + "|" + path;
    }

    private static String localName(Node node) {
        String name = node.getLocalName() != null ? node.getLocalName() : node.getNodeName();
        int colon = name.indexOf(':');
        return colon >= 0 ? name.substring(colon + 1) : name;
    }

    /**
     * The pack-relative path of a document the source parsed. The source parses from a stream, so the DOM keeps no
     * location; the file is found by parsing every XML file in the pack with the source's own default parser and
     * requiring exactly one equal tree. Two equal files are refused rather than guessed between.
     */
    private String documentPath(Document document) {
        String known = documents.get(document);
        if (known != null) return known;
        List<String> matches = new ArrayList<String>();
        try {
            final List<Path> files = new ArrayList<Path>();
            Files.walk(Paths.get(".")).forEach(new java.util.function.Consumer<Path>() {
                public void accept(Path item) {
                    if (Files.isRegularFile(item) && item.getFileName().toString().toLowerCase(Locale.ROOT).endsWith(".xml")) files.add(item);
                }
            });
            for (Path file : files) {
                Document candidate = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(file.toFile());
                if (candidate.getDocumentElement().isEqualNode(document.getDocumentElement())) matches.add(packPath(file));
            }
        } catch (Exception failure) {
            throw new IllegalStateException("V1_REFKEY_DOCUMENT " + failure, failure);
        }
        if (matches.size() != 1) throw new IllegalStateException("V1_REFKEY_DOCUMENT_AMBIGUOUS " + matches);
        documents.put(document, matches.get(0));
        return matches.get(0);
    }

    private static String packPath(Path path) {
        if (path == null) return null;
        String text = path.normalize().toString().replace('\\', '/');
        while (text.startsWith("./")) text = text.substring(2);
        return text;
    }

    // ------------------------------------------------------------------ the controller

    public void beforeStep() { stepInputPending = true; }

    public void afterStep(List<Mascot> active, List<Mascot> added, List<Mascot> removed) { }

    public boolean start(String imageSet) { return false; }

    public DraftParityHooks.Snapshot snapshot() {
        Object[] window = windowAt(tick);
        return new DraftParityHooks.Snapshot(screenBounds, workAreaRect, cursorAt(tick),
            (Rectangle)window[0], (Boolean)window[1], false, (String)window[2]);
    }

    private Point cursorAt(int at) {
        Point result = new Point(0, 0);
        for (Object item : array(scenario.get("input"))) {
            Map<String, Object> entry = object(item);
            if (integer(entry.get("tick")) <= at && entry.containsKey("cursor")) result = point(entry.get("cursor"));
        }
        return result;
    }

    private int windowEntryAt(int at) {
        int latest = -1;
        List<Object> entries = array(scenario.get("activeWindow"));
        for (int i = 0; i < entries.size(); i++) if (integer(object(entries.get(i)).get("tick")) <= at) latest = i;
        return latest;
    }

    private Object[] windowAt(int at) {
        int latest = windowEntryAt(at);
        if (latest < 0) return new Object[] { null, Boolean.FALSE, "" };
        Map<String, Object> entry = object(array(scenario.get("activeWindow")).get(latest));
        boolean shown = Boolean.TRUE.equals(entry.get("visible")) && entry.containsKey("rect");
        Rectangle rect = null;
        if (shown) {
            List<Object> edges = array(entry.get("rect"));
            rect = new Rectangle(integer(edges.get(0)), integer(edges.get(1)),
                integer(edges.get(2)) - integer(edges.get(0)), integer(edges.get(3)) - integer(edges.get(1)));
        }
        return new Object[] { rect, shown, entry.get("title") == null ? "" : (String)entry.get("title") };
    }

    public double random(String site) {
        if ("script".equals(site)) {
            // CONTRACT section 2 names a script draw script:<refKey>#<attribute>#<k>; that attribution is not built.
            throw new IllegalStateException("V1_RNG_SCRIPT_SITE_UNATTRIBUTED");
        }
        double value;
        String kind = (String)rng.get("kind");
        if ("tape".equals(kind)) {
            if (tapeCursor < tape.size()) value = tape.get(tapeCursor++);
            else { tapeExhausted = true; value = 0.5; }
        } else if ("splitmix64".equals(kind)) {
            long z = (splitmix += 0x9E3779B97F4A7C15L);
            z = (z ^ (z >>> 30)) * 0xBF58476D1CE4E5B9L;
            z = (z ^ (z >>> 27)) * 0x94D049BB133111EBL;
            z = z ^ (z >>> 31);
            value = (z >>> 11) * 0x1.0p-53;
        } else {
            value = 0.5;   // a directed scenario's draw is logged and judged rng-unexpected-draw; 0.5 keeps the run going
        }
        draws++;
        if (site.endsWith("no-candidate-drop-x") || site.endsWith("disabled-behavior-drop-x")) fallbackPending = true;
        Map<String, Object> record = event(contextSid(), "rng-draw");
        record.put("site", site);
        record.put("occurrence", draws);
        record.put("value", value);
        record.put("purpose", site.endsWith("behavior-select") ? "behavior-selection" : site.endsWith("drop-x") ? "drop-in" : site.substring(8));
        emit(record);
        return value;
    }

    public void observe(String hook, Object subject, Object detail) {
        try { handle(hook, subject, detail); }
        catch (RuntimeException failure) { throw failure; }
        catch (Exception failure) { throw new IllegalStateException("V1_OBSERVE " + hook, failure); }
    }

    private void handle(String hook, Object subject, Object detail) throws Exception {
        if (hook.equals("behavior-builder") || hook.equals("action-builder") || hook.equals("action-ref")) {
            elements.put(subject, (Element)read(detail, "element"));
        } else if (hook.equals("behavior-build")) {
            pendingBuilder = subject;
            pendingCause = "forced";
            StackTraceElement[] frames = Thread.currentThread().getStackTrace();
            for (int i = 0; i + 1 < frames.length; i++) {
                if (frames[i].getClassName().endsWith(".BehaviorBuilder") && frames[i].getMethodName().equals("buildBehavior")) {
                    // The source chose this entry only when buildNextBehavior called its builder directly.
                    if (frames[i + 1].getClassName().endsWith(".Configuration") && frames[i + 1].getMethodName().equals("buildNextBehavior")) pendingCause = "chosen";
                    break;
                }
            }
        } else if (hook.equals("user-behavior")) {
            behaviourOrigin.put(subject, new Object[] { pendingBuilder, fallbackPending ? "fallback" : pendingCause });
            pendingBuilder = null;
            fallbackPending = false;
        } else if (hook.equals("ref-enter")) {
            refStack.add(new Object[] { subject, Boolean.FALSE });
        } else if (hook.equals("ref-exit")) {
            refStack.remove(refStack.size() - 1);
        } else if (hook.equals("action-variables")) {
            VariableMap variables = (VariableMap)detail;
            Object[] top = refStack.isEmpty() ? null : refStack.get(refStack.size() - 1);
            // A reference names only the activation it builds directly; an inline descendant keeps its own element.
            if (top != null && !(Boolean)top[1]) { actionOrigin.put(variables, top[0]); top[1] = Boolean.TRUE; }
            else actionOrigin.put(variables, subject);
            actionDeclaration.put(variables, subject);
        } else if (hook.equals("action-enter")) {
            if (UNOBSERVED.contains(subject.getClass().getSimpleName())) {
                // Their scan-target, interact and sound events have no seam yet: refuse rather than trace a gap.
                throw new IllegalStateException("V1_UNOBSERVED_ACTION " + subject.getClass().getSimpleName());
            }
            entered.put(subject, ++enterSequence);
        } else if (hook.equals("behavior")) {
            behaviourEntered((Mascot)subject, detail);
        } else if (hook.equals("image-set")) {
            pendingTransform.put((Mascot)subject, new String[] { ((Mascot)subject).getImageSet(), (String)detail });
        } else if (hook.equals("build-by-name")) {
            namedBuild = (String)subject;
            String[] transform = pendingTransform.remove((Mascot)detail);
            if (transform != null) {
                Map<String, Object> record = event(sid((Mascot)detail), "transform");
                record.put("fromImageSet", transform[0]);
                record.put("toImageSet", transform[1]);
                record.put("behavior", subject);
                emit(record);
            }
        } else if (hook.equals("pose")) {
            if (read(detail, "sound") != null && Main.getInstance().getMascotBooleanSetting("Sounds", (Mascot)subject)) {
                throw new IllegalStateException("V1_UNOBSERVED_SOUND");
            }
            appliedPose.put((Mascot)subject, (Pose)detail);
        } else if (hook.equals("birth-request")) {
            birthRequested((Mascot)subject, (Mascot)detail);
        } else if (hook.equals("activate")) {
            Map<String, Object> record = event(sid((Mascot)subject), "birth-activate");
            record.put("child", sid((Mascot)subject));
            emit(record);
        } else if (hook.equals("remove")) {
            emit(event(sid((Mascot)subject), "dispose"));
        } else if (hook.equals("dispose-request")) {
            disposeRequested((Mascot)subject);
        } else if (hook.equals("error")) {
            Map<String, Object> record = event(contextSid(), "error");
            record.put("message", detail == null ? null : detail.toString());
            emit(record);
        }
    }

    private void behaviourEntered(Mascot mascot, Object behaviour) {
        if (!sids.containsKey(mascot)) {
            // A child under construction: its sid is allocated now, before its behaviour initialises, and committed
            // only when the manager queues it (CONTRACT section 3, birth lineage).
            Mascot parent = actor();
            if (parent == null) throw new IllegalStateException("V1_ORPHAN_MASCOT");
            int next = (ordinals.containsKey(parent) ? ordinals.get(parent) : 0) + 1;
            sids.put(mascot, sid(parent) + ".b" + next);
            tentative.put(mascot, Boolean.TRUE);
        }
        Object[] origin = behaviourOrigin.get(behaviour);
        if (origin == null) throw new IllegalStateException("V1_BEHAVIOUR_UNBOUND " + behaviour);
        String cause = (String)origin[1];
        Mascot current = DraftParityHooks.currentMascot();
        if ("fallback".equals(cause)) cause = "fallback";
        else if (current != null && current != mascot && managed(mascot)) cause = "partner";
        else if ("chosen".equals(cause)) cause = rootBehaviours.containsKey(origin[0]) ? "select" : "next";
        String name = (String)read(behaviour, "name");
        Map<String, Object> record = event(sid(mascot), "behavior");
        record.put("from", lastBehaviour.get(mascot));
        record.put("to", name);
        record.put("cause", cause);
        emit(record);
        lastBehaviour.put(mascot, name);
    }

    private void birthRequested(Mascot child, Mascot parent) {
        if (startup) return;   // CONTRACT section 4 step 3: start companions join without a birth event
        if (parent == null) throw new IllegalStateException("V1_BIRTH_WITHOUT_PARENT");
        int ordinal = (ordinals.containsKey(parent) ? ordinals.get(parent) : 0) + 1;
        int attempt = (attempts.containsKey(parent) ? attempts.get(parent) : 0) + 1;
        ordinals.put(parent, ordinal);
        attempts.put(parent, attempt);
        String childSid = sid(parent) + ".b" + ordinal;
        if (sids.containsKey(child) && !childSid.equals(sids.get(child))) throw new IllegalStateException("V1_BIRTH_SID_MISMATCH " + sids.get(child));
        sids.put(child, childSid);
        tentative.remove(child);
        Map<String, Object> record = event(sid(parent), "birth-request");
        record.put("child", childSid);
        record.put("imageSet", child.getImageSet());
        record.put("behavior", child.getBehavior() == null ? null : read(child.getBehavior(), "name"));
        record.put("anchor", pair(child.getAnchor().x, child.getAnchor().y));
        record.put("lookRight", child.isLookRight());
        record.put("ordinal", ordinal);
        record.put("attempt", attempt);
        emit(record);
    }

    private void disposeRequested(Mascot mascot) {
        if (startup) throw new IllegalStateException("V1_START_COMPANION_DISPOSED");
        if (!sids.containsKey(mascot) || tentative.containsKey(mascot)) {
            // A child the source built and disposed before queueing it: a refused attempt that consumes no sid.
            Mascot parent = DraftParityHooks.currentMascot();
            if (parent == null) throw new IllegalStateException("V1_REFUSED_BIRTH_WITHOUT_PARENT");
            int attempt = (attempts.containsKey(parent) ? attempts.get(parent) : 0) + 1;
            attempts.put(parent, attempt);
            sids.remove(mascot);
            tentative.remove(mascot);
            Map<String, Object> record = event(sid(parent), "birth-refused");
            record.put("child", null);
            record.put("attempt", attempt);
            record.put("reason", "error");
            emit(record);
            return;
        }
        String reason = "error";
        for (StackTraceElement frame : Thread.currentThread().getStackTrace()) {
            if (frame.getClassName().endsWith(".action.SelfDestruct")) { reason = "self-destruct"; break; }
        }
        disposing.put(mascot, Boolean.TRUE);
        Map<String, Object> record = event(sid(mascot), "dispose-request");
        record.put("reason", reason);
        emit(record);
    }

    // ------------------------------------------------------------------ input (CONTRACT section 1)

    private void dispatchInput() throws Exception {
        Point now = cursorAt(tick);
        Point before = cursorAt(tick - 1);
        if (held != null && !now.equals(before)) mouse(held, MouseEvent.MOUSE_DRAGGED, now);
        for (Object item : array(scenario.get("input"))) {
            Map<String, Object> entry = object(item);
            if (integer(entry.get("tick")) != tick || !entry.containsKey("sid")) continue;
            Mascot target = null;
            for (Map.Entry<Mascot, String> known : sids.entrySet()) if (known.getValue().equals(entry.get("sid"))) target = known.getKey();
            boolean press = "left".equals(entry.get("press")) || "left".equals(entry.get("click"));
            boolean release = "left".equals(entry.get("release")) || "left".equals(entry.get("click"));
            if (target == null || !active().contains(target)) {
                if (press || release) throw new IllegalStateException("V1_INPUT_TARGET_ABSENT " + entry.get("sid"));
                continue;
            }
            if (press) { mouse(target, MouseEvent.MOUSE_PRESSED, now); held = target; }
            if (release) { mouse(target, MouseEvent.MOUSE_RELEASED, now); held = null; }
        }
    }

    /**
     * Delivers one synthetic mouse event to the source's own listeners on the companion's (never displayed) window,
     * with the point made window-local against where the window was last drawn. drag and hotspot events are written
     * before the behaviour event the input causes, at this dispatch point (the host's emission points).
     */
    private void mouse(Mascot mascot, int id, Point screen) throws Exception {
        Component component = ((TranslucentWindow)read(mascot, "window")).asComponent();
        int x = screen.x - component.getX(), y = screen.y - component.getY();
        int mark = pending.size();
        boolean wasDragging = mascot.isDragging(), wasHotspot = mascot.isHotspotClicked();
        int behaviourEvents = countBehaviourEvents(mascot, mark);
        namedBuild = null;
        Mascot saved = context;
        context = mascot;
        try {
            if (id == MouseEvent.MOUSE_DRAGGED) {
                MouseEvent e = new MouseEvent(component, id, 0L, InputEvent.BUTTON1_DOWN_MASK, x, y, 0, false, MouseEvent.NOBUTTON);
                for (MouseMotionListener listener : component.getMouseMotionListeners()) listener.mouseDragged(e);
                return;
            }
            boolean press = id == MouseEvent.MOUSE_PRESSED;
            MouseEvent e = new MouseEvent(component, id, 0L, press ? InputEvent.BUTTON1_DOWN_MASK : 0, x, y, 1, false, MouseEvent.BUTTON1);
            for (MouseListener listener : component.getMouseListeners()) {
                if (press) listener.mousePressed(e); else listener.mouseReleased(e);
            }
            List<Map<String, Object>> inserted = new ArrayList<Map<String, Object>>();
            if (press && mascot.isHotspotClicked()) {
                Map<String, Object> record = event(sid(mascot), "hotspot");
                record.put("op", "press");
                record.put("behavior", namedBuild);
                inserted.add(record);
            } else if (press && countBehaviourEvents(mascot, mark) > behaviourEvents) {
                Map<String, Object> record = event(sid(mascot), "drag");
                record.put("op", "start");
                inserted.add(record);
            }
            if (!press && wasHotspot) {
                Map<String, Object> record = event(sid(mascot), "hotspot");
                record.put("op", "release");
                record.put("behavior", null);
                inserted.add(record);
            }
            if (!press && wasDragging) {
                Map<String, Object> record = event(sid(mascot), "drag");
                record.put("op", "end");
                inserted.add(record);
            }
            eventCount += inserted.size();
            if (observing) pending.addAll(mark, inserted);
        } finally {
            context = saved;
        }
    }

    private int countBehaviourEvents(Mascot mascot, int from) {
        int count = 0;
        String sid = sids.get(mascot);
        for (int i = from; i < pending.size(); i++) {
            Map<String, Object> record = pending.get(i);
            if ("behavior".equals(record.get("kind")) && sid.equals(record.get("sid"))) count++;
        }
        return count;
    }

    // ------------------------------------------------------------------ the scenario's desktop and native factory

    /** The scenario desktop: areas Set every tick, one active window as ScenarioEnvironment models it. */
    final class V1Environment extends Environment {
        private final Area workArea = new Area();
        private final Area window = new Area();
        private Rectangle desktopWindow = new Rectangle();
        private boolean shown;
        private boolean minimized;
        private String title = "";

        public void init() { tick(); }

        public void tick() {
            screen.set(screenBounds);
            complexScreen.set(screenMap);
            cursor.set(cursorAt(V1ScenarioRunner.this.tick));
            workArea.set(workAreaRect);
            int entry = windowEntryAt(V1ScenarioRunner.this.tick);
            if (entry != appliedWindowEntry) {
                // Only a new timeline entry replaces the desktop's window; a move or minimise holds until then.
                appliedWindowEntry = entry;
                Object[] state = windowAt(V1ScenarioRunner.this.tick);
                desktopWindow = state[0] == null ? new Rectangle() : new Rectangle((Rectangle)state[0]);
                shown = (Boolean)state[1];
                minimized = false;
                title = (String)state[2];
            }
            boolean found = usable();
            window.setVisible(found);
            window.set(found ? desktopWindow : new Rectangle());
            if (stepInputPending) {
                // CONTRACT section 4 step 1: the step's own environment refresh applies its input, once.
                stepInputPending = false;
                try { dispatchInput(); }
                catch (RuntimeException failure) { throw failure; }
                catch (Exception failure) { throw new IllegalStateException("V1_INPUT", failure); }
            }
        }

        public Area getWorkArea() { return workArea; }
        public Area getActiveIE() { return window; }
        public String getActiveIETitle() { return usable() ? title : ""; }

        private boolean usable() { return shown && !minimized && desktopWindow.intersects(screenBounds); }

        public void moveActiveIE(Point point) {
            if (!usable()) return;
            Rectangle before = new Rectangle(desktopWindow);
            desktopWindow = new Rectangle(point.x, point.y, window.getWidth(), window.getHeight());
            Map<String, Object> record = event(contextSid(), "window-move");
            record.put("from", ltrb(before));
            record.put("to", ltrb(desktopWindow));
            emit(record);
        }

        public void minimizeActiveIE() {
            if (!usable()) return;
            minimized = true;
            emit(event(contextSid(), "window-minimize"));
        }

        public void restoreIE() {
            if (shown && !minimized && !desktopWindow.intersects(screenBounds)) {
                desktopWindow = new Rectangle(workArea.getLeft() + 25, workArea.getTop() + 25, desktopWindow.width, desktopWindow.height);
            }
        }

        public void refreshCache() { }
        public void dispose() { }
    }

    /** A translucent window that is only a panel: bounds and visibility are stored, nothing is ever displayed. */
    static final class V1Window extends JPanel implements TranslucentWindow {
        private static final long serialVersionUID = 1L;
        public Component asComponent() { return this; }
        public void setImage(NativeImage image) { }
        public void updateImage() { }
        public void dispose() { }
        public void setAlwaysOnTop(boolean onTop) { }
        public void bringToFront() { }
    }

    static final class V1Image implements NativeImage {
        public void dispose() { }
    }

    final class V1Factory extends NativeFactory {
        private final Environment environment = new V1Environment();
        public Environment getEnvironment() { return environment; }
        public NativeImage newNativeImage(BufferedImage image, boolean mirror) throws OutOfMemoryException { return new V1Image(); }
        public TranslucentWindow newTransparentWindow() { return new V1Window(); }
    }

    // ------------------------------------------------------------------ the run

    private void configure() throws Exception {
        Main main = Main.getInstance();
        java.util.Properties settings = main.getProperties();
        // NativeFactory's static initialiser instantiates the parity package factory; it is replaced before any
        // mascot exists, so neither it nor the virtual display it wraps is ever initialised or shown.
        settings.setProperty("Environment", "parity");
        double dpi = ((Number)scenario.get("dpiScale")).doubleValue();
        settings.setProperty("Scaling", Double.toString(dpi));
        settings.setProperty("Opacity", "1.0");
        settings.setProperty("Filter", "false");
        Map<String, Object> flags = object(scenario.get("settings"));
        for (Map.Entry<String, Object> flag : flags.entrySet()) {
            if (flag.getValue() instanceof Boolean && !flag.getKey().startsWith("behaviour:")) {
                settings.setProperty(Character.toUpperCase(flag.getKey().charAt(0)) + flag.getKey().substring(1), flag.getValue().toString());
            }
        }
        if (scenario.get("typeSettings") != null) {
            for (Map.Entry<String, Object> type : object(scenario.get("typeSettings")).entrySet()) {
                for (Map.Entry<String, Object> flag : object(type.getValue()).entrySet()) {
                    if (flag.getValue() instanceof Boolean && !flag.getKey().startsWith("behaviour:")) {
                        settings.setProperty(Character.toUpperCase(flag.getKey().charAt(0)) + flag.getKey().substring(1) + "." + type.getKey(), flag.getValue().toString());
                    }
                }
            }
        }
        field(Main.class, "platform").set(main, Platform.x86_64);
        field(Main.class, "languageBundle").set(main, ResourceBundle.getBundle("language", Locale.UK, new Utf8ResourceBundleControl(false)));
        manager().setExitOnLastRemoved(false);
        NativeFactory.getInstance();
        field(NativeFactory.class, "instance").set(null, new V1Factory());
        Method load = Main.class.getDeclaredMethod("loadConfiguration", String.class);
        load.setAccessible(true);
        for (Object item : array(scenario.get("mascots"))) {
            String set = (String)object(item).get("imageSet");
            if (main.getConfiguration(set) == null && !(Boolean)load.invoke(main, set)) throw new IllegalStateException("V1_CONFIGURATION_REFUSED " + set);
        }
        @SuppressWarnings("unchecked")
        Map<String, Configuration> configurations = (Map<String, Configuration>)read(main, "configurations");
        for (Map.Entry<String, Configuration> item : configurations.entrySet()) {
            String set = item.getKey();
            for (Object builder : ((Map<?, ?>)read(item.getValue(), "actionBuilders")).values()) registerAction(builder, set);
            for (Object builder : ((Map<?, ?>)read(item.getValue(), "behaviorBuilders")).values()) {
                rootBehaviours.put(builder, Boolean.TRUE);
                registerBehaviour(builder, set);
            }
        }
        applyBehaviourToggles(configurations.keySet());
    }

    private void registerAction(Object builder, String set) {
        builderSets.put(builder, set);
        if (!builder.getClass().getSimpleName().equals("ActionBuilder")) return;   // an ActionRef
        for (Object animation : (List<?>)read(builder, "animationBuilders")) {
            List<?> poses = (List<?>)read(animation, "poses");
            for (int i = 0; i < poses.size(); i++) poseIndex.put((Pose)poses.get(i), i);
        }
        for (Object child : (List<?>)read(builder, "actionRefs")) registerAction(child, set);
    }

    private void registerBehaviour(Object builder, String set) {
        builderSets.put(builder, set);
        for (Object next : (List<?>)read(builder, "nextBehaviorBuilders")) registerBehaviour(next, set);
    }

    /** A scenario's behaviour toggles ("behaviour:<name>": false) become the source's DisabledBehaviours.<set>. */
    private void applyBehaviourToggles(Collection<String> sets) {
        Map<String, Object> global = object(scenario.get("settings"));
        Map<String, Object> types = scenario.get("typeSettings") == null ? new LinkedHashMap<String, Object>() : object(scenario.get("typeSettings"));
        for (String set : sets) {
            Map<String, Object> merged = new LinkedHashMap<String, Object>(global);
            if (types.containsKey(set)) merged.putAll(object(types.get(set)));
            StringBuilder disabled = new StringBuilder();
            for (Map.Entry<String, Object> flag : merged.entrySet()) {
                if (flag.getKey().startsWith("behaviour:") && Boolean.FALSE.equals(flag.getValue())) {
                    if (disabled.length() > 0) disabled.append('/');
                    disabled.append(flag.getKey().substring("behaviour:".length()));
                }
            }
            if (disabled.length() > 0) Main.getInstance().getProperties().setProperty("DisabledBehaviours." + set, disabled.toString());
        }
    }

    private String run() throws Exception {
        configure();
        tick = 0;
        List<Mascot> starts = new ArrayList<Mascot>();
        Set<String> seen = new LinkedHashSet<String>();
        for (Object item : array(scenario.get("mascots"))) {
            Map<String, Object> entry = object(item);
            String sid = (String)entry.get("sid");
            String behaviourName = (String)entry.get("behavior");
            if (!seen.add(sid) || behaviourName == null || behaviourName.isEmpty()) throw new IllegalStateException("V1_SCENARIO_INVALID start " + sid);
            Mascot mascot = new Mascot((String)entry.get("imageSet"));
            sids.put(mascot, sid);
            mascot.setAnchor(point(entry.get("anchor")));
            mascot.setLookRight(Boolean.TRUE.equals(entry.get("lookRight")));
            context = mascot;
            Configuration configuration = Main.getInstance().getConfiguration(mascot.getImageSet());
            mascot.setBehavior(configuration.buildBehavior(behaviourName, mascot));
            context = null;
            starts.add(mascot);
        }
        // CONTRACT section 4 step 3: every start companion is initialised, then all join without a step.
        for (Mascot mascot : starts) manager().add(mascot);
        @SuppressWarnings("unchecked")
        List<Mascot> mascots = (List<Mascot>)read(manager(), "mascots");
        @SuppressWarnings("unchecked")
        Set<Mascot> added = (Set<Mascot>)read(manager(), "added");
        mascots.addAll(added);
        added.clear();
        startup = false;
        flush();
        if (observing) writeStates();
        for (tick = 1; tick <= ticks; tick++) {
            manager().parityStep();
            flush();
            if (observing || tick == ticks) writeStates();
        }
        tick = ticks;
        for (Window window : Window.getWindows()) {
            if (window.isDisplayable()) throw new IllegalStateException("V1_WINDOW_DISPLAYED " + window.getClass().getName());
        }
        return "complete";
    }

    public static void main(String[] args) {
        int exit = 1;
        DraftTraceWriter writer = null;
        V1ScenarioRunner runner = null;
        String status = "error";
        try {
            if (!DraftParityHooks.enabled()) throw new IllegalStateException("V1_SEAMS_DISABLED");
            if (!Boolean.getBoolean("shimeji.parity.continueAfterError")) throw new IllegalStateException("V1_ERROR_MODE");
            String header = new String(Files.readAllBytes(Paths.get(args[0])), StandardCharsets.UTF_8).trim();
            String json = new String(Files.readAllBytes(Paths.get(args[1])), StandardCharsets.UTF_8);
            ScriptEngine engine = new ScriptEngineManager().getEngineByName("nashorn");
            engine.put("scenarioText", json);
            Map<String, Object> scenario = object(engine.eval("Java.asJSONCompatible(JSON.parse(scenarioText))"));
            if (!SCHEMA.equals(scenario.get("schema"))) throw new IllegalStateException("V1_SCENARIO_INVALID schema");
            OutputStreamWriter stream = new OutputStreamWriter(new FileOutputStream(new File(args[2])), StandardCharsets.UTF_8);
            stream.write(header + "\n");
            stream.flush();
            writer = new DraftTraceWriter(stream);
            runner = new V1ScenarioRunner(scenario, writer, args.length < 4 || !"off".equals(args[3]));
            DraftParityHooks.install(runner);
            status = runner.run();
            exit = 0;
        } catch (Throwable failure) {
            failure.printStackTrace(System.err);
            // A refusal of this runner (V1_...) aborts the run; anything else, the source's own exceptions included, is
            // an error. The two stay apart in the footer so neither is read as the other.
            for (Throwable cause = failure; cause != null; cause = cause.getCause()) {
                if (cause.getMessage() != null && cause.getMessage().startsWith("V1_")) {
                    status = "aborted";
                    System.err.println("V1_REFUSAL " + cause.getMessage());
                    break;
                }
            }
        }
        try {
            if (writer != null && runner != null) {
                runner.flush();
                Map<String, Object> footer = new LinkedHashMap<String, Object>();
                footer.put("type", "footer");
                footer.put("ticks", runner.ticks);
                footer.put("status", status);
                footer.put("draws", runner.observing ? runner.draws : 0);
                footer.put("states", runner.states);
                footer.put("events", runner.observing ? runner.eventCount : 0);
                if (!runner.observing) {
                    // The trace-off run writes only its final tick; its draw and event totals are kept for the proof.
                    footer.put("unobservedDraws", runner.draws);
                    footer.put("unobservedEvents", runner.eventCount);
                }
                if (runner.tapeExhausted) footer.put("tapeExhausted", true);
                writer.writeRecord(footer);
                writer.close();
            }
        } catch (Throwable failure) {
            failure.printStackTrace(System.err);
            exit = 1;
        }
        System.exit(exit);
    }
}
