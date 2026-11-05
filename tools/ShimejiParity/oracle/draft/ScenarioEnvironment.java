package com.group_finity.mascot.parity;

import com.group_finity.mascot.environment.Area;
import com.group_finity.mascot.environment.Environment;
import java.awt.Point;
import java.awt.Rectangle;

/** Single-window feasibility adapter; it never enumerates or moves native windows. */
public final class ScenarioEnvironment extends Environment {
    private final Environment display;
    private final Area workArea = new Area();
    private final Area window = new Area();
    private Rectangle desktopWindow = new Rectangle();
    private Rectangle desktopScreen = new Rectangle();
    private boolean shown;
    private boolean minimized;
    private String title = "";

    public ScenarioEnvironment(Environment display) { this.display = display; }

    public void init() { if (display != null) display.init(); tick(); }

    public void tick() {
        DraftParityHooks.Snapshot state = DraftParityHooks.snapshot();
        screen.set(state.screen);
        complexScreen.set(state.screens);
        cursor.set(state.cursor);
        workArea.set(state.workArea);
        desktopScreen = new Rectangle(state.screen);
        if (state.windowChanged) {
            desktopWindow = state.window == null ? new Rectangle() : new Rectangle(state.window);
            shown = state.window != null && state.windowVisible;
            minimized = false;
            title = state.windowTitle == null ? "" : state.windowTitle;
        }
        boolean found = usable();
        window.setVisible(found);
        // A failed native lookup leaves the freshly allocated RECTs zeroed in the reference environment.
        // Set also clears the previous tick's deltas, including while no window is found.
        window.set(found ? desktopWindow : new Rectangle());
    }

    public Area getWorkArea() { return workArea; }
    public Area getActiveIE() { return window; }
    public String getActiveIETitle() { return usable() ? title : ""; }

    private boolean usable() { return shown && !minimized && desktopWindow.intersects(desktopScreen); }

    public void moveActiveIE(Point point) {
        if (!usable()) return;
        Rectangle before = new Rectangle(desktopWindow);
        desktopWindow = new Rectangle(point.x, point.y, window.getWidth(), window.getHeight());
        DraftParityHooks.observe("window-move", before, new Rectangle(desktopWindow));
    }

    public void minimizeActiveIE() {
        if (!usable()) return;
        minimized = true;
        DraftParityHooks.observe("window-minimize", null, null);
    }

    public void restoreIE() {
        // The reference restores shown offscreen windows, rather than undoing a previous move or minimization.
        if (shown && !minimized && !desktopWindow.intersects(desktopScreen)) {
            desktopWindow = new Rectangle(workArea.getLeft() + 25, workArea.getTop() + 25,
                                          desktopWindow.width, desktopWindow.height);
        }
    }

    public void refreshCache() { }
    public void dispose() { if (display != null) display.dispose(); }
}
