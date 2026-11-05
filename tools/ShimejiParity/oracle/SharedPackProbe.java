package com.group_finity.mascot.oracle;

import com.group_finity.mascot.Main;
import com.group_finity.mascot.Manager;
import com.group_finity.mascot.Mascot;
import com.group_finity.mascot.NativeFactory;
import com.group_finity.mascot.Platform;
import com.group_finity.mascot.config.Configuration;
import com.group_finity.mascot.config.Entry;
import com.group_finity.mascot.environment.Area;
import com.group_finity.mascot.environment.Environment;
import com.group_finity.mascot.exception.OutOfMemoryException;
import com.group_finity.mascot.image.ImagePairs;
import com.group_finity.mascot.image.MascotImage;
import com.group_finity.mascot.image.NativeImage;
import com.group_finity.mascot.image.TranslucentWindow;
import com.joconner.i18n.Utf8ResourceBundleControl;
import java.awt.Dimension;
import java.awt.Point;
import java.awt.Rectangle;
import java.awt.Window;
import java.awt.image.BufferedImage;
import java.io.File;
import java.io.PrintWriter;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.IdentityHashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Properties;
import java.util.ResourceBundle;
import javax.script.ScriptEngine;
import javax.script.ScriptEngineManager;
import javax.swing.JFrame;
import javax.swing.JPanel;
import javax.swing.SwingUtilities;
import javax.xml.parsers.DocumentBuilderFactory;

/** A directed feasibility driver over the unchanged runtime, not a v1 trace producer. */
public final class SharedPackProbe {
    private static final String[] SETS = { "Alpha", "Beta", "Egg", ".Ball" };
    private static final IdentityHashMap<Mascot, String> IDS = new IdentityHashMap<Mascot, String>();
    private static final IdentityHashMap<MascotImage, String> IMAGES = new IdentityHashMap<MascotImage, String>();
    private static PrintWriter output;
    private static Manager manager;
    private static Method step;
    private static int birthOrdinal;

    private SharedPackProbe() { }

    private static Object field(Object owner, String name) throws Exception {
        Field item = owner.getClass().getDeclaredField(name);
        item.setAccessible(true);
        return item.get(owner);
    }

    private static void set(Object owner, String name, Object value) throws Exception {
        Field item = owner.getClass().getDeclaredField(name);
        item.setAccessible(true);
        item.set(owner, value);
    }

    private static void require(boolean value, String label) {
        if (!value) throw new AssertionError("SharedPackProbe:" + label);
    }

    private static String quote(String text) {
        return "\"" + text.replace("\\", "\\\\").replace("\"", "\\\"")
            .replace("\n", "\\n").replace("\r", "\\r").replace("\t", "\\t") + "\"";
    }

    private static String sha(File file) throws Exception {
        byte[] digest = MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(file.toPath()));
        StringBuilder result = new StringBuilder();
        for (byte item : digest) result.append(String.format("%02x", item & 255));
        return result.toString();
    }

    private static int integer(Object value) { return ((Number)value).intValue(); }

    @SuppressWarnings("unchecked")
    private static Map<String, Object> object(Object value) { return (Map<String, Object>)value; }

    @SuppressWarnings("unchecked")
    private static List<Object> array(Object value) { return (List<Object>)value; }

    private static Rectangle rectangle(Object value) {
        Map<String, Object> item = object(value);
        return new Rectangle(integer(item.get("x")), integer(item.get("y")), integer(item.get("w")), integer(item.get("h")));
    }

    private static Point point(Object value) {
        List<Object> pair = array(value);
        return new Point(integer(pair.get(0)), integer(pair.get(1)));
    }

    /** The logical environment never invokes the source native environment tick/init. */
    private static final class FixedEnvironment extends Environment {
        private final Area work = new Area();
        private final Area window = new Area();
        FixedEnvironment(Map<String, Object> scenario) {
            Map<String, Rectangle> screens = new LinkedHashMap<String, Rectangle>();
            Rectangle bounds = null;
            int index = 0;
            for (Object entry : array(scenario.get("screens"))) {
                Rectangle current = rectangle(entry);
                screens.put("scenario-screen-" + index++, current);
                bounds = bounds == null ? current : bounds.union(current);
            }
            require(bounds != null, "scenario-screen");
            screen.set(bounds);
            complexScreen.set(screens);
            work.set(rectangle(scenario.get("workArea")));
            List<Object> input = array(scenario.get("input"));
            List<Object> windows = array(scenario.get("activeWindow"));
            require(input.size() == 1 && integer(object(input.get(0)).get("tick")) == 0, "fixed-input-only");
            require(windows.size() == 1 && integer(object(windows.get(0)).get("tick")) == 0
                && Boolean.FALSE.equals(object(windows.get(0)).get("visible")), "hidden-window-only");
            require(object(input.get(0)).size() == 2, "cursor-only-no-buttons");
            cursor.set(point(object(input.get(0)).get("cursor")));
            window.set(new Rectangle(-500, -500, 0, 0));
            window.setVisible(false);
        }
        public Area getWorkArea() { return work; }
        public Area getActiveIE() { return window; }
        public String getActiveIETitle() { return ""; }
        public void moveActiveIE(Point position) { throw new AssertionError("unexpected-window-effect"); }
        public void minimizeActiveIE() { throw new AssertionError("unexpected-window-effect"); }
        public void restoreIE() { throw new AssertionError("unexpected-window-effect"); }
        public void refreshCache() { }
        public void init() { }
        public void tick() { }
        public void dispose() { }
    }

    private static final class FixedFactory extends NativeFactory {
        private final NativeFactory delegate;
        private final Environment environment;
        FixedFactory(NativeFactory delegate, Environment environment) { this.delegate = delegate; this.environment = environment; }
        public Environment getEnvironment() { return environment; }
        public NativeImage newNativeImage(BufferedImage image, boolean mirror) throws OutOfMemoryException { return delegate.newNativeImage(image, mirror); }
        public TranslucentWindow newTransparentWindow() { return delegate.newTransparentWindow(); }
    }

    private static void drain() throws Exception { SwingUtilities.invokeAndWait(new Runnable() { public void run() { } }); }

    @SuppressWarnings("unchecked")
    private static List<Mascot> active() throws Exception { return new ArrayList<Mascot>((List<Mascot>)field(manager, "mascots")); }

    @SuppressWarnings("unchecked")
    private static List<Mascot> pending() throws Exception { return new ArrayList<Mascot>((java.util.Set<Mascot>)field(manager, "added")); }

    @SuppressWarnings("unchecked")
    private static void configure(final Map<String, Object> scenario) throws Exception {
        Main main = Main.getInstance();
        Properties settings = main.getProperties();
        settings.setProperty("Environment", "virtual");
        settings.setProperty("Scaling", "1.0");
        settings.setProperty("Opacity", "1.0");
        Map<String, Object> flags = object(scenario.get("settings"));
        for (String key : new String[] { "Breeding", "Transients", "Transformation", "Throwing", "Sounds", "Multiscreen" }) {
            settings.setProperty(key, flags.get(key.toLowerCase(Locale.ROOT)).toString());
        }
        set(main, "platform", Platform.x86_64);
        set(main, "languageBundle", ResourceBundle.getBundle("language", Locale.UK, new Utf8ResourceBundleControl(false)));
        manager = (Manager)field(main, "manager");
        manager.setExitOnLastRemoved(false);
        step = Manager.class.getDeclaredMethod("tick");
        step.setAccessible(true);
        NativeFactory delegate = NativeFactory.getInstance();
        final JFrame display = (JFrame)field(delegate.getEnvironment(), "display");
        final Rectangle screen = rectangle(array(scenario.get("screens")).get(0));
        // Initialize only this owned display. Calling VirtualEnvironment.init would sample MouseInfo.
        SwingUtilities.invokeAndWait(new Runnable() { public void run() {
            JPanel content = new JPanel(null);
            content.setPreferredSize(new Dimension(screen.width, screen.height));
            display.setContentPane(content);
            display.setAutoRequestFocus(false);
            display.setTitle("Shimeji isolated shared-pack feasibility");
            display.pack();
            display.setVisible(true);
        } });
        Field singleton = NativeFactory.class.getDeclaredField("instance");
        singleton.setAccessible(true);
        singleton.set(null, new FixedFactory(delegate, new FixedEnvironment(scenario)));
        DocumentBuilderFactory xml = DocumentBuilderFactory.newInstance();
        xml.setFeature("http://apache.org/xml/features/disallow-doctype-decl", true);
        Map<String, Configuration> configurations = (Map<String, Configuration>)field(main, "configurations");
        for (String name : SETS) {
            if (!Files.isDirectory(Paths.get("img", name, "conf"))) continue;
            Configuration configuration = new Configuration();
            for (String filename : new String[] { "actions.xml", "behaviors.xml" }) {
                configuration.load(new Entry(xml.newDocumentBuilder().parse(Paths.get("img", name, "conf", filename).toFile()).getDocumentElement()), name);
            }
            configuration.validate();
            configurations.put(name, configuration);
            for (int image = 1; image <= 5; image++) {
                String path = Paths.get(".", "img", name, "shime" + image + ".png").toString();
                for (boolean right : new boolean[] { false, true }) IMAGES.put(ImagePairs.getImage(path, right), name + "/shime" + image + ".png");
            }
        }
    }

    private static String id(Mascot mascot) {
        if (!IDS.containsKey(mascot)) IDS.put(mascot, "m1.b" + ++birthOrdinal);
        return IDS.get(mascot);
    }

    private static String state(Mascot mascot, boolean member) {
        String image = mascot.getImage() == null ? null : IMAGES.get(mascot.getImage());
        require(mascot.getImage() == null || image != null, "stored-image-known");
        return "{\"sid\":" + quote(id(mascot)) + ",\"member\":" + member + ",\"imageSet\":" + quote(mascot.getImageSet())
            + ",\"age\":" + mascot.getTime() + ",\"anchor\":[" + mascot.getAnchor().x + "," + mascot.getAnchor().y
            + "],\"lookRight\":" + mascot.isLookRight() + ",\"image\":" + (image == null ? "null" : quote(image))
            + ",\"behavior\":" + quote(mascot.getBehavior().toString()) + "}";
    }

    private static void snapshot(int tick) throws Exception {
        StringBuilder states = new StringBuilder();
        for (Mascot mascot : active()) { if (states.length() > 0) states.append(','); states.append(state(mascot, true)); }
        for (Mascot mascot : pending()) { if (states.length() > 0) states.append(','); states.append(state(mascot, false)); }
        output.println("{\"record\":\"step\",\"tick\":" + tick + ",\"active\":" + active().size()
            + ",\"pending\":" + pending().size() + ",\"states\":[" + states + "]}");
        output.flush();
    }

    private static void run(Map<String, Object> scenario) throws Exception {
        List<Object> roots = array(scenario.get("mascots"));
        require(roots.size() == 1, "one-directed-root");
        Map<String, Object> start = object(roots.get(0));
        Mascot root = new Mascot((String)start.get("imageSet"));
        IDS.put(root, (String)start.get("sid"));
        root.setAnchor(point(start.get("anchor")));
        root.setLookRight((Boolean)start.get("lookRight"));
        root.setBehavior(Main.getInstance().getConfiguration(root.getImageSet()).buildBehavior((String)start.get("behavior"), root));
        manager.add(root);
        drain();
        snapshot(0);
        String name = (String)scenario.get("id");
        boolean reachedTarget = false;
        boolean bred = false;
        boolean activated = false;
        for (int tick = 1; tick <= integer(scenario.get("ticks")); tick++) {
            step.invoke(manager);
            drain();
            snapshot(tick);
            if (name.equals("S-slice-stand-walk-01") && root.getAnchor().x == 600) reachedTarget = true;
            if (name.equals("S-slice-transform-01") && tick == 18) {
                require(root.getImageSet().equals("Beta") && active().get(0) == root, "transform-same-entity-at-duration");
                require(root.getAnchor().equals(point(start.get("anchor"))), "transform-anchor-retained");
            }
            if (name.equals("S-slice-breed-01")) {
                for (Mascot child : pending()) {
                    require(child.getImageSet().equals(".Ball"), "hidden-member-spawned");
                    require(child.getAnchor().equals(new Point(680, 680)), "birth-offset-before-activation");
                    bred = true;
                }
                for (Mascot child : active()) if (child != root) {
                    require(child.getImageSet().equals(".Ball") && child.getTime() > 0, "child-independent-active");
                    activated = true;
                }
            }
        }
        if (name.equals("S-slice-stand-walk-01")) require(reachedTarget, "walk-exact-target-reached");
        if (name.equals("S-slice-transform-01")) require(root.getImageSet().equals("Beta"), "transform-target-remains");
        if (name.equals("S-slice-breed-01")) require(bred && activated, "birth-queued-and-activated");
        require(root.getTime() == integer(scenario.get("ticks")), "real-manager-all-steps");
    }

    private static void cleanup() throws Exception {
        if (manager != null) {
            manager.stop();
            manager.disposeAll();
            if (step != null) step.invoke(manager);
            require(manager.getCount() == 0, "clean-manager-empty");
        }
        SwingUtilities.invokeAndWait(new Runnable() { public void run() { for (Window window : Window.getWindows()) window.dispose(); } });
        for (Window window : Window.getWindows()) require(!window.isDisplayable(), "clean-owned-windows-closed");
    }

    public static void main(String[] args) {
        int status = 1;
        try {
            output = new PrintWriter(new File(args[0]), "UTF-8");
            File runtime = new File(Mascot.class.getProtectionDomain().getCodeSource().getLocation().toURI());
            require(sha(runtime).equals(args[1]), "expected-runtime-artifact");
            String json = new String(Files.readAllBytes(Paths.get(args[2])), StandardCharsets.UTF_8);
            ScriptEngine engine = new ScriptEngineManager().getEngineByName("nashorn");
            engine.put("scenarioText", json);
            Map<String, Object> scenario = object(engine.eval("Java.asJSONCompatible(JSON.parse(scenarioText))"));
            require("shimeji-parity-scenario/1".equals(scenario.get("schema")), "scenario-schema");
            require("none".equals(object(scenario.get("rng")).get("kind")), "source-none-only-uncontrolled-rng");
            require(((Number)scenario.get("dpiScale")).doubleValue() == 1.0, "unit-scaling-only");
            output.println("{\"record\":\"header\",\"schema\":\"oracle-feasibility/0\",\"runtimeSha256\":" + quote(args[1])
                + ",\"scenarioSha256\":" + quote(sha(new File(args[2]))) + ",\"rngControlled\":false}");
            configure(scenario);
            run(scenario);
            cleanup();
            output.println("{\"record\":\"complete\",\"status\":\"pass\",\"ownedWindowsClosed\":true}");
            status = 0;
        } catch (Throwable failure) {
            failure.printStackTrace(System.err);
            try { cleanup(); } catch (Throwable nested) { nested.printStackTrace(System.err); }
            if (output != null) output.println("{\"record\":\"complete\",\"status\":\"fail\",\"error\":" + quote(failure.toString()) + "}");
        } finally { if (output != null) output.close(); }
        System.exit(status);
    }
}
