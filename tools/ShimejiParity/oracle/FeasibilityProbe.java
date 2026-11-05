package com.group_finity.mascot.oracle;

import com.group_finity.mascot.Main;
import com.group_finity.mascot.Manager;
import com.group_finity.mascot.Mascot;
import com.group_finity.mascot.NativeFactory;
import com.group_finity.mascot.Platform;
import com.group_finity.mascot.action.ActionBase;
import com.group_finity.mascot.config.Configuration;
import com.group_finity.mascot.config.Entry;
import com.group_finity.mascot.environment.Environment;
import com.group_finity.mascot.image.ImagePairs;
import com.group_finity.mascot.parity.DraftPoseGeometry;
import com.joconner.i18n.Utf8ResourceBundleControl;
import java.awt.Point;
import java.awt.Window;
import java.io.File;
import java.io.PrintWriter;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Properties;
import java.util.ResourceBundle;
import javax.swing.SwingUtilities;
import javax.xml.parsers.DocumentBuilderFactory;

/** A separate driver, never a replacement for any upstream runtime class. */
public final class FeasibilityProbe {
    private static final IdentityHashMap<Mascot, String> IDS = new IdentityHashMap<Mascot, String>();
    private static int nextBirth = 1;
    private static PrintWriter output;
    private static Manager manager;
    private static Environment environment;
    private static Method tick;

    private FeasibilityProbe() { }

    private static Object field(Object owner, String name) throws Exception {
        Field field = owner.getClass().getDeclaredField(name);
        field.setAccessible(true);
        return field.get(owner);
    }

    private static void setField(Object owner, String name, Object value) throws Exception {
        Field field = owner.getClass().getDeclaredField(name);
        field.setAccessible(true);
        field.set(owner, value);
    }

    private static void require(boolean condition, String label) {
        if (!condition) throw new AssertionError("FeasibilityProbe:" + label);
    }

    private static String quote(String text) {
        return "\"" + text.replace("\\", "\\\\").replace("\"", "\\\"")
            .replace("\n", "\\n").replace("\r", "\\r") + "\"";
    }

    private static String fingerprint(File file) throws Exception {
        byte[] digest = MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(file.toPath()));
        StringBuilder result = new StringBuilder();
        for (byte value : digest) result.append(String.format("%02x", value & 255));
        return result.toString();
    }

    private static String id(Mascot mascot) {
        if (!IDS.containsKey(mascot)) IDS.put(mascot, "birth" + nextBirth++);
        return IDS.get(mascot);
    }

    private static void drainUi() throws Exception {
        SwingUtilities.invokeAndWait(new Runnable() { public void run() { } });
    }

    @SuppressWarnings("unchecked")
    private static List<Mascot> active() throws Exception {
        return new ArrayList<Mascot>((List<Mascot>)field(manager, "mascots"));
    }

    @SuppressWarnings("unchecked")
    private static List<Mascot> pending() throws Exception {
        return new ArrayList<Mascot>((java.util.Set<Mascot>)field(manager, "added"));
    }

    private static String imageKey(Mascot mascot) {
        if (mascot.getImage() == null) return "hidden";
        for (String type : new String[] { "A", "B" }) {
            for (String image : new String[] { "tile", "alternate" }) {
                String path = Paths.get(".", "img", type, image + ".png").toString();
                if (ImagePairs.getImage(path, mascot.isLookRight()) == mascot.getImage()) return type + "/" + image;
            }
        }
        return "other";
    }

    private static String state(Mascot mascot, boolean member) {
        // These controlled fixtures first apply a pose on their first manager tick.
        Map<String, Object> geometry = DraftPoseGeometry.observe(mascot, mascot.getTime() > 0);
        return "{\"entity\":" + quote(id(mascot)) + ",\"member\":" + member
            + ",\"type\":" + quote(mascot.getImageSet()) + ",\"age\":" + mascot.getTime()
            + ",\"x\":" + mascot.getAnchor().x + ",\"y\":" + mascot.getAnchor().y
            + ",\"right\":" + mascot.isLookRight() + ",\"image\":" + quote(imageKey(mascot))
            + ",\"origin\":" + geometry.get("origin") + ",\"poseAnchor\":" + geometry.get("poseAnchor")
            + ",\"behavior\":" + quote(mascot.getBehavior().toString()) + "}";
    }

    private static void snapshot(String scenario, int step) throws Exception {
        List<String> states = new ArrayList<String>();
        for (Mascot mascot : active()) states.add(state(mascot, true));
        for (Mascot mascot : pending()) states.add(state(mascot, false));
        StringBuilder body = new StringBuilder();
        for (String item : states) { if (body.length() > 0) body.append(','); body.append(item); }
        output.println("{\"record\":\"step\",\"scenario\":" + quote(scenario) + ",\"step\":" + step
            + ",\"active\":" + active().size() + ",\"pending\":" + pending().size()
            + ",\"states\":[" + body + "]}");
        output.flush();
    }

    @SuppressWarnings("unchecked")
    private static void load(String name) throws Exception {
        Configuration configuration = new Configuration();
        DocumentBuilderFactory factory = DocumentBuilderFactory.newInstance();
        factory.setFeature("http://apache.org/xml/features/disallow-doctype-decl", true);
        for (String file : new String[] { "actions.xml", "behaviors.xml" }) {
            File path = Paths.get("img", name, "conf", file).toFile();
            configuration.load(new Entry(factory.newDocumentBuilder().parse(path).getDocumentElement()), name);
        }
        configuration.validate();
        ((Map<String, Configuration>)field(Main.getInstance(), "configurations")).put(name, configuration);
    }

    private static void configure() throws Exception {
        Main main = Main.getInstance();
        Properties properties = main.getProperties();
        properties.setProperty("Environment", "virtual");
        properties.setProperty("WindowSize", "640x480");
        properties.setProperty("Scaling", "1.0");
        properties.setProperty("Opacity", "1.0");
        properties.setProperty("Sounds", "false");
        properties.setProperty("Throwing", "false");
        properties.setProperty("Breeding", "true");
        properties.setProperty("Transients", "true");
        properties.setProperty("Transformation", "true");
        setField(main, "platform", Platform.x86_64);
        setField(main, "languageBundle", ResourceBundle.getBundle("language", Locale.UK, new Utf8ResourceBundleControl(false)));
        manager = (Manager)field(main, "manager");
        manager.setExitOnLastRemoved(false);
        tick = Manager.class.getDeclaredMethod("tick");
        tick.setAccessible(true);
        environment = NativeFactory.getInstance().getEnvironment();
        environment.init();
        drainUi();
        load("A");
        load("B");
    }

    private static void run(String scenario, int expectedOffset) throws Exception {
        Mascot root = new Mascot("A");
        IDS.put(root, "root");
        root.setAnchor(new Point(120, 180));
        root.setLookRight(scenario.equals("breed"));
        root.getVariables().put("marker", 123);
        String entry = scenario.equals("sequence") ? "SequenceFixture" : scenario.equals("breed") ? "BreedFixture" : "TransformFixture";
        root.setBehavior(Main.getInstance().getConfiguration("A").buildBehavior(entry, root));
        manager.add(root);
        drainUi();
        snapshot(scenario, -1);
        for (int step = 0; step < 8; step++) {
            tick.invoke(manager);
            drainUi();
            snapshot(scenario, step);
            if (scenario.equals("sequence")) {
                if (step == 0) require(imageKey(root).equals("A/tile"), "sequence-first-pose");
                if (step == 1) require(imageKey(root).equals("A/alternate"), "sequence-second-pose");
                if (step == 2) {
                    require(root.getAnchor().equals(new Point(120 + expectedOffset, 184)), "sequence-anchor");
                    require(root.isLookRight(), "sequence-facing");
                }
            } else if (scenario.equals("breed")) {
                if (step == 3) require(active().size() == 1 && pending().size() == 2, "breed-queued-before-activation");
                if (step == 4) {
                    require(active().size() == 3 && pending().isEmpty(), "breed-activated-next-step");
                    for (Mascot child : active()) {
                        if (child == root) continue;
                        require(child.getAnchor().equals(new Point(113, 185)) && child.isLookRight(), "breed-offset-facing");
                        Object action = field(child.getBehavior(), "action");
                        require(((ActionBase)action).isDraggable(), "breed-independent-draggable-action");
                    }
                }
            } else if (step == 2) {
                require(root.getImageSet().equals("B") && active().get(0) == root, "transform-identity-type");
                require(root.getAnchor().equals(new Point(120, 180)) && !root.isLookRight(), "transform-anchor-facing");
                require(root.getVariables().get("marker").equals(123), "transform-variable-retention");
                require(root.getCount() == 1 && root.getTotalCount() == 1, "transform-counts");
            }
        }
        require(root.getTime() == 8, "source-manager-ticks-executed");
        require(!imageKey(root).equals("hidden") && !imageKey(root).equals("other"), "source-image-applied");
        int displayed = 0;
        for (Window window : Window.getWindows()) if (window.isDisplayable() && window.isVisible()) displayed++;
        require(displayed > 0, "source-virtual-window-created");
        int before = root.getTime();
        manager.start();
        long deadline = System.nanoTime() + 2000000000L;
        while (root.getTime() < before + 2 && System.nanoTime() < deadline) Thread.sleep(10);
        manager.stop();
        require(root.getTime() >= before + 2, "source-timer-loop-advanced");
        output.println("{\"record\":\"witness\",\"scenario\":" + quote(scenario)
            + ",\"managerTicks\":8,\"timerAdvanced\":true,\"visibleOwnedWindows\":" + displayed
            + ",\"screenCapture\":\"not_performed\"}");
    }

    private static void cleanup() throws Exception {
        if (manager != null) {
            manager.stop();
            manager.disposeAll();
            if (tick != null) tick.invoke(manager);
            require(manager.getCount() == 0, "clean-manager-empty");
        }
        if (environment != null) environment.dispose();
        SwingUtilities.invokeAndWait(new Runnable() {
            public void run() { for (Window window : Window.getWindows()) window.dispose(); }
        });
        for (Window window : Window.getWindows()) require(!window.isDisplayable(), "clean-owned-windows-closed");
    }

    public static void main(String[] args) {
        int status = 1;
        try {
            output = new PrintWriter(new File(args[0]), "UTF-8");
            File runtime = new File(Mascot.class.getProtectionDomain().getCodeSource().getLocation().toURI());
            require(fingerprint(runtime).equals(args[1]), "expected-runtime-artifact");
            output.println("{\"record\":\"header\",\"schema\":\"oracle-feasibility/0\",\"runtimeSha256\":"
                + quote(args[1]) + ",\"geometrySchema\":\"oracle-pose-geometry/1\",\"driver\":\"separate-unmodified-runtime-probe\"}");
            configure();
            run(args[2], args.length > 3 ? Integer.parseInt(args[3]) : 3);
            cleanup();
            output.println("{\"record\":\"complete\",\"status\":\"pass\",\"ownedWindowsClosed\":true}");
            status = 0;
        } catch (Throwable failure) {
            failure.printStackTrace(System.err);
            try { cleanup(); } catch (Throwable cleanupFailure) { cleanupFailure.printStackTrace(System.err); }
            if (output != null) output.println("{\"record\":\"complete\",\"status\":\"fail\",\"error\":" + quote(failure.toString()) + "}");
        } finally {
            if (output != null) output.close();
        }
        System.exit(status);
    }
}
