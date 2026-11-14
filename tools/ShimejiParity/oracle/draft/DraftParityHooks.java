package com.group_finity.mascot.parity;

import com.group_finity.mascot.Mascot;
import com.group_finity.mascot.environment.Environment;
import java.awt.Point;
import java.awt.Rectangle;
import java.util.ArrayList;
import java.util.Collection;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import javax.script.Bindings;
import javax.script.ScriptEngine;
import javax.script.ScriptException;

/** Draft test seam. Protocol assembly belongs to the versioned-contract adapter. */
public final class DraftParityHooks {
    public interface Controller {
        void beforeStep();
        void afterStep(List<Mascot> active, List<Mascot> added, List<Mascot> removed);
        void observe(String hook, Object subject, Object detail);
        double random(String sourceSite);
        Snapshot snapshot();
        boolean start(String imageSet);
    }

    public static final class Snapshot {
        public final Rectangle screen;
        public final Rectangle workArea;
        public final Point cursor;
        public final Map<String, Rectangle> screens;
        public final Rectangle window;
        public final boolean windowVisible;
        public final boolean windowChanged;
        public final String windowTitle;

        public Snapshot(Rectangle screen, Rectangle workArea, Point cursor, Rectangle window,
                        boolean visible, boolean changed, String title) {
            this.screen = new Rectangle(screen);
            this.workArea = new Rectangle(workArea);
            this.cursor = new Point(cursor);
            this.screens = new LinkedHashMap<String, Rectangle>();
            this.screens.put("scenario-screen", new Rectangle(screen));
            this.window = window == null ? null : new Rectangle(window);
            this.windowVisible = visible;
            this.windowChanged = changed;
            this.windowTitle = title;
        }
    }

    private static Controller controller;
    private static Mascot actor;
    private DraftParityHooks() { }

    public static boolean enabled() { return Boolean.getBoolean("shimeji.parity.enabled"); }

    public static void install(Controller value) {
        if (controller != null || value == null) throw new IllegalStateException("oracle-controller-install");
        controller = value;
    }

    private static Controller required() {
        if (controller == null) throw new IllegalStateException("oracle-controller-missing");
        return controller;
    }

    public static void beforeStep() { if (enabled()) required().beforeStep(); }
    public static void beforeMascot(Mascot value) { if (enabled()) actor = value; }
    public static void afterMascot() { actor = null; }
    public static Mascot currentMascot() { return actor; }

    public static void afterStep(Collection<Mascot> active, Collection<Mascot> added, Collection<Mascot> removed) {
        if (enabled()) required().afterStep(new ArrayList<Mascot>(active), new ArrayList<Mascot>(added), new ArrayList<Mascot>(removed));
    }

    public static void observe(String hook, Object subject, Object detail) {
        if (enabled()) required().observe(hook, subject, detail);
    }

    // Mute observation is passive and total. Hook failures are producer defects, never source exceptions.
    // Values here are the local already evaluated by Mute.apply; no hook calls a parameter or a Clip method.
    private static final List<String> muteDefects = new ArrayList<String>();

    private static void observeMute(String hook, Object action, Object detail) {
        try {
            if (enabled()) required().observe(hook, action, detail);
        } catch (Throwable failure) {
            try { muteDefects.add("MUTE_OBSERVER_FAILED " + hook + ": " + failure.getClass().getName()); }
            catch (Throwable ignored) { }
        }
    }

    public static void muteSound(Object action, String sound) { observeMute("mute-sound", action, sound); }
    public static void muteNamed(Object action, String sound) { observeMute("mute-named", action, sound); }
    public static void muteAll(Object action) { observeMute("mute-all", action, null); }
    public static void muteAllStop(Object action) { observeMute("mute-all-stop", action, null); }
    public static void muteDone(Object action) { observeMute("mute-done", action, null); }

    public static void muteLookup(Object action, int tier, Object clips) {
        try { observeMute("mute-lookup", action, new Object[] { Integer.valueOf(tier), clips }); }
        catch (Throwable failure) {
            try { muteDefects.add("MUTE_OBSERVER_FAILED mute-lookup: " + failure.getClass().getName()); }
            catch (Throwable ignored) { }
        }
    }

    public static void muteStop(Object action, Object clip) { observeMute("mute-stop", action, clip); }

    /** Completion validation reads only the runner's stored observation, never source or clip state. */
    public static void muteIncomplete(long entry) {
        try {
            String defect = "MUTE_INCOMPLETE_ENTRY " + entry;
            if (!muteDefects.contains(defect)) muteDefects.add(defect);
        } catch (Throwable ignored) { }
    }

    /** Named producer defects are checked even when a source exception escapes before run returns. */
    public static List<String> muteDefects() { return new ArrayList<String>(muteDefects); }

    public static double random(String sourceSite) {
        if (!enabled()) return Math.random();
        double value = required().random(sourceSite);
        if (!(value >= 0.0 && value < 1.0)) throw new IllegalStateException("oracle-rng-range");
        return value;
    }

    public static void prepareScript(ScriptEngine engine, Bindings context) throws ScriptException {
        if (!enabled()) return;
        // Install into the actual Bindings used by CompiledScript.eval. Engine-scope
        // installation alone does not cover Nashorn's per-Bindings globals.
        engine.eval("Math.random = function() { return Java.type('com.group_finity.mascot.parity.DraftParityHooks').random('script'); };", context);
    }

    public static boolean startOverride(String imageSet) {
        if (!enabled()) return false;
        if (!required().start(imageSet)) throw new IllegalStateException("oracle-start-not-handled " + imageSet);
        return true;
    }

    /** A scan's result, passed through unchanged: the controller sees which companion the source chose. */
    public static <T> T scanned(Object action, T target) {
        observe("scan-target", action, target);
        return target;
    }

    public static Snapshot snapshot() { return required().snapshot(); }
    public static Rectangle initialScreen() { return new Rectangle(snapshot().screen); }

    public static void refreshDisplay(Environment environment) {
        Snapshot state = snapshot();
        environment.screen.set(state.screen);
        environment.complexScreen.set(state.screens);
        environment.cursor.set(state.cursor);
    }

    /**
     * Main.showError's seam: the dialog never opens. By default the run fails fast, as the feasibility probes
     * expect. The v1 runner sets shimeji.parity.continueAfterError: the source carries on once its error dialog is
     * dismissed, so the error becomes a trace event and the caller returns before the dialog.
     */
    public static void fail(String message) {
        fail(message, null);
    }

    /** Optional context must be supplied at the actual failure/catch site, never recovered from its message. */
    public static void fail(String message, Fault fault) {
        observe("error", fault, message);
        if (!Boolean.getBoolean("shimeji.parity.continueAfterError")) {
            throw new IllegalStateException("oracle-runtime-error " + message);
        }
    }

    /**
     * Stored fault data for the versioned writer. A missing channel is inconclusive, not a guessed cause. The
     * factory does not inspect an action, run a script or read a diagnostic. A reviewed failure-site hook owns the
     * code and operands it passes here; a reviewed catch site owns the wrapper list.
     */
    public static final class Fault {
        private final String code;
        private final Map<String, Object> lookup;
        private final List<String> via;

        public Fault(String code, Map<String, Object> lookup, List<String> via) {
            this.code = code;
            this.lookup = lookup == null ? null : new LinkedHashMap<String, Object>(lookup);
            this.via = new ArrayList<String>(via);
        }

        public Map<String, Object> record() {
            Map<String, Object> result = new LinkedHashMap<String, Object>();
            result.put("code", code);
            result.put("lookup", lookup == null ? null : new LinkedHashMap<String, Object>(lookup));
            result.put("via", new ArrayList<String>(via));
            return result;
        }
    }

    // ------------------------------------------------------------------ trace/2 typed-fault attribution
    // Java 6 source level. Every failure-site hook below is total: it
    // never throws, never wraps or replaces the exception it is given, and writes only this class's own fields.
    // A failure inside a hook is recorded in faultDefects() for the runner to refuse at run end by name.
    //
    // Bindings are keyed by object identity, so they are made only on an instance unique to one failure:
    //  - A throwable with an empty stack trace is never bound. HotSpot's fast-throw path (OmitStackTraceInFastThrow,
    //    on by default) raises one preallocated, shared instance per implicit exception class once hot code is
    //    compiled, and that instance has no stack trace. Binding it would type unrelated later faults. The hook
    //    records FAULT_BIND_EMPTY_STACK_TRACE and the fault stays unclassified.
    //  - A binding is never overwritten. Binding an object that already holds one records FAULT_BIND_ALREADY_BOUND
    //    and keeps the first binding.
    //  - A binding lives until a reader takes it: arm, armStep and carryCause remove every link of the cause chain
    //    they read, so a binding is reported at most once and their reported cause chains retain no binding.

    /** Optional: a controller that can resolve operands from stored provenance. Absent means unclassified. */
    public interface FaultResolver {
        /** False for trace/1: do not read, bind or diagnose the new typed channel. */
        boolean faultChannelEnabled();
        /**
         * Returns the lookup operands for the given code, or null when stored provenance cannot supply them.
         * Must read stored state only (Main.configurations by identity, recorded action origins); it must not
         * evaluate a parameter, condition or script, emit a record or allocate a sid. It may throw a provenance
         * refusal the runner already uses (for example an unbound action); that is recorded, not propagated.
         *   behavior-missing / action-missing: owner = the Configuration searched, operand = requested name
         *   parameter-invalid: owner = the initialized action, operand = canonical schema key
         *   scan-target-missing: owner = the initialized action, operand = null
         */
        Map<String, Object> faultLookup(String code, Object owner, Object operand);
    }

    private static boolean faultChannelEnabled() {
        return enabled() && controller instanceof FaultResolver && ((FaultResolver)controller).faultChannelEnabled();
    }

    private static final java.util.Set<String> WRAPPERS = new java.util.HashSet<String>(java.util.Arrays.asList(
        "startup", "press", "release", "birth", "transform", "arrive", "interact"));
    /** A typed cause bound to one thrown instance at its failure site. */
    private static final class BoundCause {
        final String code;
        final Map<String, Object> lookup;
        BoundCause(String code, Map<String, Object> lookup) { this.code = code; this.lookup = lookup; }
    }

    private static final Map<Throwable, BoundCause> boundCauses = new java.util.IdentityHashMap<Throwable, BoundCause>();
    private static final List<String> faultDefects = new ArrayList<String>();
    private static Fault armedFault;

    /** Records a named defect: NAME [detail][: failure]. The name is what the runner refuses by. */
    private static void faultDefect(String name, String detail, Throwable failure) {
        try {
            faultDefects.add(name + (detail == null ? "" : " " + detail) + (failure == null ? "" : ": " + failure));
        } catch (Throwable ignored) { }
    }

    /**
     * True when thrown may take a binding: its identity belongs to this failure alone (non-empty stack trace) and
     * it holds no binding yet. Otherwise records the named defect and returns false.
     */
    private static boolean bindable(Throwable thrown, String code) {
        StackTraceElement[] trace = thrown.getStackTrace();
        if (trace == null || trace.length == 0) {
            faultDefect("FAULT_BIND_EMPTY_STACK_TRACE", code + " " + thrown.getClass().getName(), null);
            return false;
        }
        if (boundCauses.containsKey(thrown)) {
            faultDefect("FAULT_BIND_ALREADY_BOUND", code + " " + thrown.getClass().getName(), null);
            return false;
        }
        return true;
    }

    private static <T extends Throwable> T bindCause(T thrown, String code, Object owner, Object operand) {
        try {
            if (thrown == null || !faultChannelEnabled()) return thrown;
            if (!bindable(thrown, code)) return thrown;
            Map<String, Object> lookup = ((FaultResolver)controller).faultLookup(code, owner, operand);
            if (lookup != null) boundCauses.put(thrown, new BoundCause(code, new LinkedHashMap<String, Object>(lookup)));
        } catch (Throwable failure) {
            faultDefect("FAULT_BIND_FAILED", code, failure);
        }
        return thrown;
    }

    /** A behaviour requested by name is not a root behaviour of the Configuration searched. */
    public static <T extends Throwable> T behaviorMissing(T thrown, Object configuration, String requested) {
        return bindCause(thrown, "behavior-missing", configuration, requested);
    }

    /** As behaviorMissing, bound only when the stored lookup result is null. */
    public static <T extends Throwable> T nextBehaviorMissing(T thrown, Object configuration, String requested,
                                                               Object found) {
        return found == null ? bindCause(thrown, "behavior-missing", configuration, requested) : thrown;
    }

    /** A behaviour or reference names an action the Configuration searched does not declare. */
    public static <T extends Throwable> T actionMissing(T thrown, Object configuration, String requested) {
        return bindCause(thrown, "action-missing", configuration, requested);
    }

    /** The source rejected a parameter of an initialized action; canonicalKey is the source's own schema key. */
    public static <T extends Throwable> T parameterInvalid(T thrown, Object action, String canonicalKey) {
        return bindCause(thrown, "parameter-invalid", action, canonicalKey);
    }

    /** Bound only when the action's stored target field is null. */
    public static <T extends Throwable> T scanTargetMissing(T thrown, Object action, Object target) {
        return target == null ? bindCause(thrown, "scan-target-missing", action, null) : thrown;
    }

    /**
     * Moves the binding stored on caught's cause chain onto thrown. caught's links are consumed (the
     * source drops caught here), and thrown takes the binding only if bindable.
     */
    public static <T extends Throwable> T carryCause(T thrown, Throwable caught) {
        try {
            if (thrown == null || !faultChannelEnabled()) return thrown;
            BoundCause cause = consumeBinding(caught);
            if (cause != null && bindable(thrown, "carry " + cause.code)) boundCauses.put(thrown, cause);
        } catch (Throwable failure) {
            faultDefect("FAULT_CARRY_FAILED", null, failure);
        }
        return thrown;
    }

    /**
     * Returns the deepest bound link of caught's cause chain (identity, cycle-safe) and removes the binding of
     * every link walked, so no binding outlives the one report that reads it.
     */
    private static BoundCause consumeBinding(Throwable caught) {
        BoundCause found = null;
        java.util.Set<Throwable> seen = java.util.Collections.newSetFromMap(new java.util.IdentityHashMap<Throwable, Boolean>());
        for (Throwable link = caught; link != null && seen.add(link); link = link.getCause()) {
            BoundCause cause = boundCauses.remove(link);
            if (cause != null) found = cause;
        }
        return found;
    }

    /** A wrapper caught this exception and is about to report it: via [wrapper]. */
    public static void arm(String wrapper, Throwable caught) { armFault(wrapper, caught); }

    /** The companion's own step caught this exception and is about to report it: via []. */
    public static void armStep(Throwable caught) { armFault(null, caught); }

    private static void armFault(String wrapper, Throwable caught) {
        try {
            armedFault = null;
            if (!faultChannelEnabled()) return;
            BoundCause cause = consumeBinding(caught);
            List<String> via = new ArrayList<String>();
            if (wrapper != null) {
                if (!WRAPPERS.contains(wrapper)) { faultDefect("FAULT_ARM_LABEL", wrapper, null); return; }
                via.add(wrapper);
            }
            armedFault = cause == null ? new Fault("unclassified", null, via) : new Fault(cause.code, cause.lookup, via);
        } catch (Throwable failure) {
            armedFault = null;
            faultDefect("FAULT_ARM_FAILED", wrapper, failure);
        }
    }

    public static void disarm() { armedFault = null; }

    /** The armed fault, consumed once; null when no wrapper armed one (the record stays unclassified, via []). */
    public static Fault takeArmedFault() {
        Fault value = armedFault;
        armedFault = null;
        return value;
    }

    /** Hook-internal failures by name; a non-empty list must fail the run by name, never pass silently. */
    public static List<String> faultDefects() { return new ArrayList<String>(faultDefects); }

    public static Map<String, Object> faultRecord(Object context) {
        if (context instanceof Fault) return ((Fault)context).record();
        return new Fault("unclassified", null, new ArrayList<String>()).record();
    }
}
