package com.group_finity.mascot.parity;

import com.group_finity.mascot.Mascot;
import com.group_finity.mascot.behavior.Behavior;
import com.group_finity.mascot.image.MascotImage;
import com.group_finity.mascot.image.NativeImage;
import com.group_finity.mascot.image.TranslucentWindow;
import java.awt.Component;
import java.awt.Dimension;
import java.awt.Point;
import java.awt.Window;
import java.io.StringWriter;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.util.Arrays;
import java.util.Map;
import javax.swing.JPanel;
import sun.misc.Unsafe;

/** Runs unchanged Mascot getters/apply with a private in-memory window dependency. */
public final class PoseGeometryProbe {
    private static final class MemoryWindow implements TranslucentWindow {
        final JPanel panel = new JPanel();
        public Component asComponent() { return panel; }
        public void setImage(NativeImage image) { }
        public void updateImage() { }
        public void dispose() { panel.setVisible(false); }
        public void setAlwaysOnTop(boolean value) { }
        public void bringToFront() { }
    }

    private static void require(boolean value, String label) {
        if (!value) throw new AssertionError("PoseGeometryProbe:" + label);
    }

    private static void set(Object target, String name, Object value) throws Exception {
        Field field = Mascot.class.getDeclaredField(name);
        field.setAccessible(true);
        field.set(target, value);
    }

    private static void expect(Map<String, Object> state, int x, int y, String label) {
        require(Arrays.asList(x, y).equals(state.get("origin")), label);
    }

    public static void main(String[] args) throws Exception {
        System.out.println("POSE_GEOMETRY_PROBE_EXECUTED");
        Field access = Unsafe.class.getDeclaredField("theUnsafe");
        access.setAccessible(true);
        Unsafe unsafe = (Unsafe)access.get(null);
        Mascot mascot = (Mascot)unsafe.allocateInstance(Mascot.class);
        MemoryWindow window = new MemoryWindow();
        window.panel.setVisible(false);
        set(mascot, "window", window);
        set(mascot, "animating", true);
        set(mascot, "imageSet", "A");
        set(mascot, "behavior", Proxy.newProxyInstance(Behavior.class.getClassLoader(), new Class<?>[] { Behavior.class }, new InvocationHandler() {
            public Object invoke(Object proxy, Method method, Object[] values) {
                if (method.getName().equals("toString")) return "in-memory-behavior";
                throw new AssertionError("PoseGeometryProbe:observer-invoked-behavior");
            }
        }));
        mascot.setAnchor(new Point(100, 90));
        Map<String, Object> startup = DraftPoseGeometry.observe(mascot, false);
        require(startup.get("origin") == null && startup.get("poseAnchor") == null, "startup-null-geometry");
        Method formatter = Class.forName("com.group_finity.mascot.oracle.FeasibilityProbe").getDeclaredMethod("state", Mascot.class, boolean.class);
        formatter.setAccessible(true);
        String startupState = ((String)formatter.invoke(null, mascot, true)).replace(" ", "");
        require(startupState.contains("\"origin\":null") && startupState.contains("\"poseAnchor\":null"), "feasibility-writer-startup-nulls");
        MascotImage image = new MascotImage((NativeImage)null, new Point(7, 11), new Dimension(19, 23));
        mascot.setImage(image);
        mascot.apply();
        expect(DraftPoseGeometry.observe(mascot, true), 93, 79, "visible-origin");
        require(window.panel.isVisible(), "visible-image-shown");
        mascot.setImage(null);
        mascot.setAnchor(new Point(135, 150));
        mascot.apply();
        Map<String, Object> hidden = DraftPoseGeometry.observe(mascot, true);
        expect(hidden, 93, 79, "hidden-origin-frozen");
        require(hidden.get("poseAnchor") == null, "hidden-anchor-null");
        require(!window.panel.isVisible(), "hidden-window-invisible");
        set(mascot, "time", 1);
        String actualState = ((String)formatter.invoke(null, mascot, true)).replace(" ", "");
        require(actualState.contains("\"origin\":[93,79]") && actualState.contains("\"poseAnchor\":null"), "feasibility-writer-hidden-geometry");
        mascot.setAnchor(new Point(180, 210));
        mascot.apply();
        expect(DraftPoseGeometry.observe(mascot, true), 93, 79, "hidden-second-move-frozen");
        mascot.setImage(image);
        mascot.apply();
        Map<String, Object> shown = DraftPoseGeometry.observe(mascot, true);
        expect(shown, 173, 199, "reappeared-origin-updated");
        require(Arrays.asList(7, 11).equals(shown.get("poseAnchor")), "effective-image-anchor");
        require(window.panel.isVisible(), "reappeared-window-visible");
        String shownState = ((String)formatter.invoke(null, mascot, true)).replace(" ", "");
        require(shownState.contains("\"origin\":[173,199]") && shownState.contains("\"poseAnchor\":[7,11]"), "feasibility-writer-reappeared-geometry");
        StringWriter text = new StringWriter();
        new DraftTraceWriter(text).writeRecord(hidden);
        require(text.toString().contains("\"origin\":[93,79]") && text.toString().contains("\"poseAnchor\":null"), "transport-preserves-hidden-geometry");
        require(Window.getWindows().length == 0, "no-display-windows");
        System.out.println("{\"status\":\"pass\",\"nativeOperations\":0,\"displayWindows\":0,\"formatterExecuted\":true,\"hiddenOrigin\":[93,79],\"reappearedOrigin\":[173,199]}");
    }
}
