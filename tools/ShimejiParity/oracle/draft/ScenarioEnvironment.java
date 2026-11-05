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
    private Rectangle restore;
    private String title = "";

    public ScenarioEnvironment(Environment display) { this.display = display; }

    public void init() { display.init(); tick(); }

    public void tick() {
        DraftParityHooks.Snapshot state = DraftParityHooks.snapshot();
        screen.set(state.screen);
        complexScreen.set(state.screens);
        cursor.set(state.cursor);
        workArea.set(state.workArea);
        if (state.windowChanged) {
            window.set(state.window);
            window.setVisible(state.windowVisible);
            title = state.windowTitle;
            restore = null;
        }
    }

    public Area getWorkArea() { return workArea; }
    public Area getActiveIE() { return window; }
    public String getActiveIETitle() { return title; }

    public void moveActiveIE(Point point) {
        Rectangle before = window.toRectangle();
        if (restore == null) restore = new Rectangle(before);
        window.set(new Rectangle(point.x, point.y, before.width, before.height));
        DraftParityHooks.observe("window-move", before, window.toRectangle());
    }

    public void minimizeActiveIE() {
        window.setVisible(false);
        DraftParityHooks.observe("window-minimize", null, null);
    }

    public void restoreIE() {
        if (restore != null) window.set(restore);
        window.setVisible(true);
        DraftParityHooks.observe("window-restore", null, null);
    }

    public void refreshCache() { }
    public void dispose() { display.dispose(); }
}
