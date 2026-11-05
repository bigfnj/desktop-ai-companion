package com.group_finity.mascot.parity;

import com.group_finity.mascot.Mascot;
import com.group_finity.mascot.environment.Area;
import java.awt.Point;
import java.awt.Rectangle;
import java.util.ArrayList;
import java.util.List;

/** Headless checks of the simulated window desktop; no display or native window is constructed. */
public final class ScenarioEnvironmentProbe {
    private static final class Event {
        final String kind;
        final Object from;
        final Object to;
        Event(String kind, Object from, Object to) { this.kind = kind; this.from = from; this.to = to; }
    }

    private static final class Controller implements DraftParityHooks.Controller {
        private final List<Event> events = new ArrayList<Event>();
        private DraftParityHooks.Snapshot state = snapshot(null, false, true);
        public void beforeStep() { }
        public void afterStep(List<Mascot> active, List<Mascot> added, List<Mascot> removed) { }
        public void observe(String hook, Object subject, Object detail) { events.add(new Event(hook, subject, detail)); }
        public double random(String site) { throw new AssertionError("unexpected-random-draw"); }
        public boolean start(String imageSet) { return false; }
        public DraftParityHooks.Snapshot snapshot() { return state; }
        private static DraftParityHooks.Snapshot snapshot(Rectangle window, boolean visible, boolean changed) {
            return new DraftParityHooks.Snapshot(new Rectangle(0, 0, 640, 480), new Rectangle(20, 30, 600, 400),
                new Point(12, 15), window, visible, changed, "synthetic-window");
        }
        void show(Rectangle rect) { state = snapshot(rect, true, true); }
        void hold() { state = snapshot(state.window, state.windowVisible, false); }
    }

    private ScenarioEnvironmentProbe() { }
    private static void require(boolean value, String label) {
        if (!value) throw new AssertionError("ScenarioEnvironmentProbe:" + label);
    }
    private static boolean bounds(Area area, int x, int y, int width, int height) {
        return area.toRectangle().equals(new Rectangle(x, y, width, height));
    }
    private static void noRestoreEvent(Controller controller, int before) {
        require(controller.events.size() == before, "restore-has-no-invented-event");
    }

    public static void main(String[] args) {
        System.out.println("SCENARIO_ENVIRONMENT_PROBE_EXECUTED");
        require(Boolean.getBoolean("java.awt.headless"), "headless-required");
        Controller controller = new Controller();
        DraftParityHooks.install(controller);
        System.setProperty("shimeji.parity.enabled", "true");
        ScenarioEnvironment environment = new ScenarioEnvironment(null);
        environment.init();
        Area area = environment.getActiveIE();
        require(!area.isVisible() && bounds(area, 0, 0, 0, 0), "initial-null-window-zero-rect");
        environment.moveActiveIE(new Point(50, 60));
        environment.minimizeActiveIE();
        require(controller.events.isEmpty(), "null-window-noops");
        environment.restoreIE();
        noRestoreEvent(controller, 0);

        controller.show(new Rectangle(100, 80, 200, 120));
        environment.tick();
        require(area.isVisible() && bounds(area, 100, 80, 200, 120), "timeline-window-shown");
        require(area.getDleft() == 100 && area.getDtop() == 80, "initial-area-deltas");
        controller.hold();
        environment.tick();
        require(area.getDleft() == 0 && area.getDtop() == 0 && area.getDright() == 0 && area.getDbottom() == 0,
            "area-refresh-clears-deltas");

        environment.moveActiveIE(new Point(120, 90));
        require(bounds(area, 100, 80, 200, 120) && area.isVisible(), "cached-geometry-delayed");
        environment.moveActiveIE(new Point(130, 95));
        Event secondMove = controller.events.get(1);
        require(secondMove.kind.equals("window-move") && secondMove.from.equals(new Rectangle(120, 90, 200, 120))
            && secondMove.to.equals(new Rectangle(130, 95, 200, 120)), "move-reacquires-desktop-state");
        environment.tick();
        require(bounds(area, 130, 95, 200, 120) && area.getDleft() == 30 && area.getDtop() == 15,
            "next-tick-observes-desktop-move");
        environment.tick();
        require(area.getDleft() == 0 && area.getDtop() == 0, "steady-area-deltas-zero");

        environment.minimizeActiveIE();
        require(area.isVisible(), "minimize-cache-delayed");
        int minimizedEvents = controller.events.size();
        environment.minimizeActiveIE();
        environment.moveActiveIE(new Point(3, 4));
        require(controller.events.size() == minimizedEvents, "minimized-target-noops");
        environment.tick();
        require(!area.isVisible() && bounds(area, 0, 0, 0, 0), "absent-window-zero-rect");
        require(area.getDleft() == -130 && area.getDtop() == -95, "absent-window-updates-deltas");
        environment.restoreIE();
        noRestoreEvent(controller, minimizedEvents);
        environment.tick();
        require(!area.isVisible(), "restore-does-not-unminimize");
        require(area.getDleft() == 0 && area.getDtop() == 0, "absent-steady-deltas-zero");

        controller.show(new Rectangle(100, 80, 200, 120));
        environment.tick();
        require(area.isVisible(), "new-timeline-replaces-minimized-state");
        controller.hold();
        environment.moveActiveIE(new Point(800, 90));
        require(area.isVisible() && bounds(area, 100, 80, 200, 120), "offscreen-move-cache-delayed");
        int offscreenEvents = controller.events.size();
        environment.moveActiveIE(new Point(5, 6));
        environment.minimizeActiveIE();
        require(controller.events.size() == offscreenEvents, "offscreen-reacquire-noops");
        environment.tick();
        require(!area.isVisible() && bounds(area, 0, 0, 0, 0), "offscreen-removed-next-tick");
        environment.restoreIE();
        noRestoreEvent(controller, offscreenEvents);
        require(!area.isVisible(), "restore-cache-delayed");
        environment.tick();
        require(area.isVisible() && bounds(area, 45, 55, 200, 120), "restore-offscreen-to-work-area-offset");

        environment.moveActiveIE(new Point(800, 90));
        environment.tick();
        require(!area.isVisible() && bounds(area, 0, 0, 0, 0), "restore-move-starts-with-zero-cache");
        environment.restoreIE();
        int restoreMoveEvents = controller.events.size();
        environment.moveActiveIE(new Point(150, 160));
        require(controller.events.size() == restoreMoveEvents + 1, "restored-window-reacquired-before-cache-refresh");
        Event restoreMove = controller.events.get(restoreMoveEvents);
        require(restoreMove.from.equals(new Rectangle(45, 55, 200, 120))
            && restoreMove.to.equals(new Rectangle(150, 160, 0, 0)), "restore-then-move-uses-cached-size");
        environment.tick();
        require(!area.isVisible() && bounds(area, 0, 0, 0, 0), "restore-then-move-zero-region-not-found");
        environment.dispose();
        System.out.println("{\"status\":\"pass\",\"nativeOperations\":0,\"events\":" + controller.events.size()
            + ",\"assumption\":\"failed-native-lookup-leaves-zeroed-RECTs;restore-then-move-keeps-cached-zero-size-without-unprobed-native-minimum-tracking-clamp\"}");
    }
}
