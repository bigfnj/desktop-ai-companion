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
        observe("error", null, message);
        if (!Boolean.getBoolean("shimeji.parity.continueAfterError")) {
            throw new IllegalStateException("oracle-runtime-error " + message);
        }
    }
}
