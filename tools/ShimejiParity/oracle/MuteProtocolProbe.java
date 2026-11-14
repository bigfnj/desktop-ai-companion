package com.group_finity.mascot.parity;

import com.group_finity.mascot.Mascot;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Original synthetic protocol probe. It neither reads nor reproduces upstream Mute source. */
public final class MuteProtocolProbe {
    private static void require(boolean value, String witness) {
        if (!value) throw new IllegalStateException(witness);
    }
    private static Object stored(Object owner, String name) throws Exception {
        Field field = owner.getClass().getDeclaredField(name);
        field.setAccessible(true);
        return field.get(owner);
    }
    private static V1ScenarioRunner runner(boolean observing, boolean trace2) throws Exception {
        Map<String,Object> scenario = new LinkedHashMap<String,Object>();
        Map<String,Object> rng = new LinkedHashMap<String,Object>(), rect = new LinkedHashMap<String,Object>();
        rng.put("kind", "none"); scenario.put("rng", rng); scenario.put("ticks", 0);
        rect.put("x", 0); rect.put("y", 0); rect.put("w", 100); rect.put("h", 100);
        scenario.put("screens", Arrays.asList(rect)); scenario.put("workArea", rect);
        Constructor<V1ScenarioRunner> ctor = V1ScenarioRunner.class.getDeclaredConstructor(Map.class, DraftTraceWriter.class, boolean.class, boolean.class);
        ctor.setAccessible(true);
        return ctor.newInstance(scenario, new DraftTraceWriter(new java.io.StringWriter()), observing, trace2);
    }
    /** Equal objects still need separate identity records; the fields are passive source-API stand-ins. */
    private static final class Mute {
        final Mascot mascot;
        Mute(Mascot mascot) { this.mascot = mascot; }
        public boolean equals(Object other) { return other instanceof Mute; }
        public int hashCode() { return 1; }
    }
    private static final class Clip {
        public boolean isRunning() { throw new AssertionError("JAVA_MUTE_CLIP_METHOD_CALLED"); }
        public void stop() { throw new AssertionError("JAVA_MUTE_CLIP_METHOD_CALLED"); }
        public String toString() { throw new AssertionError("JAVA_MUTE_CLIP_METHOD_CALLED"); }
    }
    private static final class SoundPose { final String sound = "synthetic"; }
    @SuppressWarnings("unchecked")
    private static Mute action(V1ScenarioRunner runner, Mascot mascot) throws Exception {
        ((Map<Mascot,String>)stored(runner, "sids")).put(mascot, "m1");
        Mute action = new Mute(mascot);
        runner.observe("action-enter", action, null);
        return action;
    }
    @SuppressWarnings("unchecked")
    private static List<Map<String,Object>> events(V1ScenarioRunner runner) throws Exception {
        return (List<Map<String,Object>>)stored(runner, "pending");
    }
    private static void named(Mute action, String raw) {
        DraftParityHooks.muteSound(action, raw);
        DraftParityHooks.muteNamed(action, raw);
        List<Clip> clips = Arrays.asList(new Clip());
        DraftParityHooks.muteLookup(action, 1, clips);
        DraftParityHooks.muteLookup(action, 2, new ArrayList<Clip>());
        DraftParityHooks.muteLookup(action, 3, clips);
        DraftParityHooks.muteStop(action, clips.get(0));
        DraftParityHooks.muteDone(action);
    }
    private static void all(Mute action, boolean actualSoundsBranch) {
        DraftParityHooks.muteSound(action, null);
        DraftParityHooks.muteAll(action);
        if (actualSoundsBranch) DraftParityHooks.muteAllStop(action);
        DraftParityHooks.muteDone(action);
    }
    private static void completion(V1ScenarioRunner runner) throws Exception {
        Method validate = V1ScenarioRunner.class.getDeclaredMethod("validateMuteCompletion");
        validate.setAccessible(true); validate.invoke(runner);
    }
    private static final class Throwing implements DraftParityHooks.Controller {
        int calls;
        public void beforeStep() { }
        public void afterStep(List<Mascot> a, List<Mascot> b, List<Mascot> c) { }
        public void observe(String hook, Object subject, Object detail) { calls++; throw new AssertionError("original controller failure"); }
        public double random(String site) { throw new AssertionError("JAVA_MUTE_EXTRA_DRAW"); }
        public DraftParityHooks.Snapshot snapshot() { return null; }
        public boolean start(String set) { return false; }
    }
    /** Called by the dedicated synthetic Manager stub, including just before its simulated source exception. */
    public static void sourceStep() {
        if (Boolean.getBoolean("probe.muteDefect")) DraftParityHooks.muteNamed(new Object(), "x");
        if (Boolean.getBoolean("probe.muteIncomplete")) {
            try {
                Field controller = DraftParityHooks.class.getDeclaredField("controller"); controller.setAccessible(true);
                V1ScenarioRunner runner = (V1ScenarioRunner)controller.get(null);
                Mute action = action(runner, new Mascot("Alpha"));
                DraftParityHooks.muteSound(action, null); DraftParityHooks.muteAll(action);
            } catch (Exception failure) { throw new IllegalStateException("PROBE_MUTE_SETUP", failure); }
        }
        if (Boolean.getBoolean("probe.sourceThrow")) throw new IllegalArgumentException("PROBE_MUTE_SOURCE_FAILURE");
    }
    public static void main(String[] args) throws Exception {
        String name = args[0];
        if ("total".equals(name)) {
            Throwing controller = new Throwing(); DraftParityHooks.install(controller);
            Object action = new Object();
            DraftParityHooks.muteSound(action, "x"); DraftParityHooks.muteNamed(action, "x");
            DraftParityHooks.muteLookup(action, 1, new ArrayList<Object>()); DraftParityHooks.muteStop(action, new Clip());
            DraftParityHooks.muteAll(action); DraftParityHooks.muteAllStop(action); DraftParityHooks.muteDone(action);
            require(controller.calls == 7 && DraftParityHooks.muteDefects().size() == 7, "JAVA_MUTE_TOTAL_DEFECTS");
        } else if ("legacy".equals(name)) {
            V1ScenarioRunner legacy = runner(true, false);
            try { action(legacy, new Mascot("Alpha")); throw new AssertionError("JAVA_MUTE_TRACE1_REFUSAL"); }
            catch (IllegalStateException expected) {
                require("V1_UNOBSERVED_ACTION Mute".equals(expected.getMessage()), "JAVA_MUTE_TRACE1_REFUSAL");
            }
        } else {
            V1ScenarioRunner runner = runner(!"off".equals(name), true);
            DraftParityHooks.install(runner);
            Mascot mascot = new Mascot("Alpha"); Mute action = action(runner, mascot);
            if ("named".equals(name) || "off".equals(name)) {
                named(action, "/TO");
                require(events(runner).size() == 1 && "/TO".equals(events(runner).get(0).get("clip")), "JAVA_MUTE_RAW_NAMED");
            } else if ("empty".equals(name)) {
                named(action, "");
                require(events(runner).size() == 1 && "".equals(events(runner).get(0).get("clip")), "JAVA_MUTE_EMPTY_NAMED");
            } else if ("all-on".equals(name) || "all-off".equals(name)) {
                all(action, "all-on".equals(name));
                require(events(runner).size() == ("all-on".equals(name) ? 1 : 0), "JAVA_MUTE_NULL_BRANCH");
                if ("all-on".equals(name)) require(events(runner).get(0).containsKey("clip") && events(runner).get(0).get("clip") == null, "JAVA_MUTE_NULL_VALUE");
            } else if ("reactivation".equals(name)) {
                named(action, "/TO"); runner.observe("action-enter", action, null); named(action, "/To");
                Mute other = action(runner, mascot); named(other, "to");
                require(events(runner).size() == 3 && "/TO".equals(events(runner).get(0).get("clip"))
                    && "/To".equals(events(runner).get(1).get("clip")) && "to".equals(events(runner).get(2).get("clip")), "JAVA_MUTE_REACTIVATION_IDENTITY");
            } else if ("pre-evaluation-failure".equals(name)) {
                // A cast/script failure before O1 performs no Mute callback. No null or completion is invented.
                require(events(runner).isEmpty(), "JAVA_MUTE_NO_PRE_EVAL_INTENT");
                completion(runner);
                require(DraftParityHooks.muteDefects().isEmpty(), "JAVA_MUTE_PRE_EVAL_NOT_INCOMPLETE");
            } else if ("completion".equals(name)) {
                all(action, false); completion(runner);
                require(DraftParityHooks.muteDefects().isEmpty(), "JAVA_MUTE_COMPLETION_REQUIRED");
            } else if ("incomplete-reentry".equals(name)) {
                DraftParityHooks.muteSound(action, null); DraftParityHooks.muteAll(action);
                Object previous = ((Map<?,?>)stored(runner, "muteObservations")).get(action);
                runner.observe("action-enter", action, null);
                require(((Map<?,?>)stored(runner, "muteObservations")).get(action) == previous, "JAVA_MUTE_INCOMPLETE_REENTRY_RETAINED");
                completion(runner);
                require(DraftParityHooks.muteDefects().size() == 1
                    && DraftParityHooks.muteDefects().get(0).startsWith("MUTE_INCOMPLETE_ENTRY "), "JAVA_MUTE_INCOMPLETE_REENTRY_DEFECT");
            } else if ("bad-order".equals(name)) {
                DraftParityHooks.muteNamed(action, "/TO");
                require(events(runner).isEmpty() && DraftParityHooks.muteDefects().size() == 1, "JAVA_MUTE_ORDER_DEFECT");
            } else if ("pose-refusal".equals(name)) {
                System.setProperty("probe.soundsOn", "true");
                try { runner.observe("pose", mascot, new SoundPose()); throw new AssertionError("JAVA_MUTE_POSE_REFUSAL_KEPT"); }
                catch (IllegalStateException expected) {
                    require("V1_UNOBSERVED_SOUND".equals(expected.getMessage()), "JAVA_MUTE_POSE_REFUSAL_KEPT");
                }
            } else throw new IllegalArgumentException(name);
            if (!"bad-order".equals(name) && !"incomplete-reentry".equals(name)) {
                completion(runner);
                require(DraftParityHooks.muteDefects().isEmpty(), "JAVA_MUTE_NO_DEFECT");
            }
            if ("named".equals(name) || "off".equals(name)) {
                Method flush = V1ScenarioRunner.class.getDeclaredMethod("flush"); flush.setAccessible(true); flush.invoke(runner);
                DraftTraceWriter.EventSequence sequence = (DraftTraceWriter.EventSequence)stored(runner, "eventSequence");
                System.out.println("MUTE_DIGEST " + sequence.finish());
                require(((Integer)stored(runner, "eventCount")).intValue() == 1, "JAVA_MUTE_OFF_COUNT");
            }
        }
        System.out.println("MUTE_PROTOCOL_PROBE_EXECUTED: " + name);
    }
}
