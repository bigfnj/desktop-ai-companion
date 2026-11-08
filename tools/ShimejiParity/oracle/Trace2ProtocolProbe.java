package com.group_finity.mascot.parity;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Original protocol-only controls. The Python harness supplies synthetic runtime API stubs, never source code. */
public final class Trace2ProtocolProbe {
    private static void require(boolean value, String witness) {
        if (!value) throw new IllegalStateException(witness);
    }

    private static final class Hooks implements DraftParityHooks.Controller, DraftParityHooks.FaultResolver {
        boolean enabled = true, refused;
        int resolutions;
        public void beforeStep() { }
        public void afterStep(List<com.group_finity.mascot.Mascot> a, List<com.group_finity.mascot.Mascot> b, List<com.group_finity.mascot.Mascot> c) { }
        public void observe(String h, Object s, Object d) { }
        public double random(String s) { return 0.5; }
        public DraftParityHooks.Snapshot snapshot() { return null; }
        public boolean start(String s) { return false; }
        public boolean faultChannelEnabled() { return enabled; }
        public Map<String,Object> faultLookup(String code, Object owner, Object operand) {
            resolutions++;
            if (refused) throw new IllegalStateException("PROBE_PROVENANCE_REFUSED");
            Map<String,Object> value = new LinkedHashMap<String,Object>();
            if ("behavior-missing".equals(code) || "action-missing".equals(code)) {
                value.put("imageSet", owner); value.put("behavior-missing".equals(code) ? "behavior" : "action", operand);
            } else { value.put("refKey", owner); if ("parameter-invalid".equals(code)) value.put("parameter", operand); }
            return value;
        }
    }
    private static Map<String,Object> arm(String wrapper, Throwable value) {
        if (wrapper == null) DraftParityHooks.armStep(value); else DraftParityHooks.arm(wrapper, value);
        return DraftParityHooks.faultRecord(DraftParityHooks.takeArmedFault());
    }
    private static boolean unknown(Map<String,Object> value) { return "unclassified".equals(value.get("code")); }
    @SuppressWarnings("unchecked")
    private static Map<String,Object> operands(Map<String,Object> value) { return (Map<String,Object>)value.get("lookup"); }
    private static boolean defect(String name) { for (String d : DraftParityHooks.faultDefects()) if (d.startsWith(name)) return true; return false; }
    private static Object stored(Object owner, String name) throws Exception {
        java.lang.reflect.Field field = owner.getClass().getDeclaredField(name); field.setAccessible(true); return field.get(owner);
    }
    private static final class Action { final com.group_finity.mascot.script.VariableMap variables = new com.group_finity.mascot.script.VariableMap(); }
    @SuppressWarnings("unchecked")
    private static void resolver() throws Exception {
        Map<String,Object> scenario = new LinkedHashMap<String,Object>(), rng = new LinkedHashMap<String,Object>(), rect = new LinkedHashMap<String,Object>();
        rng.put("kind", "none"); scenario.put("rng", rng); scenario.put("ticks", 0);
        rect.put("x", 0); rect.put("y", 0); rect.put("w", 100); rect.put("h", 100);
        scenario.put("screens", java.util.Arrays.asList(rect)); scenario.put("workArea", rect);
        java.lang.reflect.Constructor<V1ScenarioRunner> ctor = V1ScenarioRunner.class.getDeclaredConstructor(Map.class, DraftTraceWriter.class, boolean.class, boolean.class);
        ctor.setAccessible(true);
        V1ScenarioRunner runner = ctor.newInstance(scenario, new DraftTraceWriter(new java.io.StringWriter()), true, true);
        Map<String,Object> configs = (Map<String,Object>)stored(com.group_finity.mascot.Main.getInstance(), "configurations");
        Object owner = new com.group_finity.mascot.config.Configuration(); configs.put("ParentMember", new com.group_finity.mascot.config.Configuration()); configs.put("SearchedMember", owner);
        for (String code : new String[] { "behavior-missing", "action-missing" }) for (String raw : new String[] { null, "", " " }) {
            Map<String,Object> value = runner.faultLookup(code, owner, raw); String key = "behavior-missing".equals(code) ? "behavior" : "action";
            require(value.size() == 2 && "SearchedMember".equals(value.get("imageSet")) && value.containsKey(key)
                    && (raw == null ? value.get(key) == null : raw.equals(value.get(key))), "JAVA_T2_RESOLVER_SEARCHED_RAW");
        }
        Action action = new Action(); Object builder = new Object(), declaration = new Object();
        org.w3c.dom.Document doc = javax.xml.parsers.DocumentBuilderFactory.newInstance().newDocumentBuilder().newDocument();
        org.w3c.dom.Element root = doc.createElement("Root"); doc.appendChild(root);
        org.w3c.dom.Element element = doc.createElement("ActionReference"), declared = doc.createElement("Action");
        root.appendChild(element); root.appendChild(declared);
        ((Map<Object,Object>)stored(runner, "actionOrigin")).put(action.variables, builder);
        ((Map<Object,Object>)stored(runner, "actionDeclaration")).put(action.variables, declaration);
        ((Map<Object,Object>)stored(runner, "elements")).put(builder, element);
        ((Map<Object,Object>)stored(runner, "elements")).put(declaration, declared);
        ((Map<Object,Object>)stored(runner, "builderSets")).put(builder, "OriginMember");
        ((Map<Object,Object>)stored(runner, "builderSets")).put(declaration, "OriginMember");
        ((Map<Object,Object>)stored(runner, "documents")).put(doc, "conf/actions.xml");
        String key = "OriginMember|conf/actions.xml|/Root[1]/ActionReference[1]";
        require(key.equals(runner.faultLookup("parameter-invalid", action, "BornCount").get("refKey"))
                && "BornCount".equals(runner.faultLookup("parameter-invalid", action, "BornCount").get("parameter"))
                && key.equals(runner.faultLookup("scan-target-missing", action, null).get("refKey")), "JAVA_T2_RESOLVER_ACTION_ORIGIN");
        configs.put("AliasMember", owner);
        try { runner.faultLookup("behavior-missing", owner, "Name"); throw new AssertionError("JAVA_T2_RESOLVER_AMBIGUOUS"); }
        catch (IllegalStateException e) { require("V1_FAULT_CONFIGURATION_AMBIGUOUS".equals(e.getMessage()), "JAVA_T2_RESOLVER_AMBIGUOUS"); }
        V1ScenarioRunner legacy = ctor.newInstance(scenario, new DraftTraceWriter(new java.io.StringWriter()), true, false);
        try {
            require(!legacy.faultChannelEnabled() && legacy.faultLookup("parameter-invalid", new Object(), "BornCount") == null, "JAVA_T1_FAULT_PROVENANCE_FREEZE");
        } catch (RuntimeException failure) { throw new IllegalStateException("JAVA_T1_FAULT_PROVENANCE_FREEZE", failure); }
    }
    private static void hooks(String name) throws Exception {
        if ("resolver".equals(name)) { resolver(); return; }
        Hooks controller = new Hooks(); controller.enabled = !"legacy".equals(name); DraftParityHooks.install(controller);
        Throwable a = new IllegalStateException("original-message"), b = new IllegalStateException("replacement");
        if ("identity".equals(name)) {
            StackTraceElement[] stack = a.getStackTrace();
            require(DraftParityHooks.behaviorMissing(a, "SearchedMember", " ") == a && "original-message".equals(a.getMessage())
                    && java.util.Arrays.equals(stack, a.getStackTrace()), "JAVA_T2_HOOK_IDENTITY");
            Map<String,Object> value = arm("birth", a);
            require("behavior-missing".equals(value.get("code")) && " ".equals(operands(value).get("behavior"))
                    && "SearchedMember".equals(operands(value).get("imageSet")) && java.util.Arrays.asList("birth").equals(value.get("via")), "JAVA_T2_HOOK_IDENTITY");
            require(DraftParityHooks.takeArmedFault() == null && unknown(arm("birth", a)), "JAVA_T2_HOOK_CONSUMED");
        } else if ("chain".equals(name)) {
            a.initCause(b); DraftParityHooks.behaviorMissing(a, "Outer", "outer"); DraftParityHooks.actionMissing(b, "Inner", "inner");
            Map<String,Object> value = arm("arrive", a);
            require("action-missing".equals(value.get("code")) && "Inner".equals(operands(value).get("imageSet"))
                    && unknown(arm(null, a)) && unknown(arm(null, b)), "JAVA_T2_HOOK_CHAIN");
        } else if ("carry".equals(name) || "carry-empty".equals(name)) {
            DraftParityHooks.behaviorMissing(a, "OldBehaviourMember", "Fall");
            if (name.endsWith("empty")) b.setStackTrace(new StackTraceElement[0]);
            require(DraftParityHooks.carryCause(b, a) == b, "JAVA_T2_HOOK_CARRY_IDENTITY");
            Map<String,Object> value = arm(null, b);
            if (name.endsWith("empty")) require(unknown(value) && defect("FAULT_BIND_EMPTY_STACK_TRACE") && unknown(arm(null, a)), "JAVA_T2_HOOK_CARRY_EMPTY");
            else require("behavior-missing".equals(value.get("code")) && "Fall".equals(operands(value).get("behavior"))
                    && ((List<?>)value.get("via")).isEmpty() && unknown(arm(null, a)), "JAVA_T2_HOOK_CARRY");
        } else if ("empty".equals(name) || "legacy".equals(name)) {
            a.setStackTrace(new StackTraceElement[0]); DraftParityHooks.behaviorMissing(a, "Member", "Name");
            require(unknown(arm("birth", a)) && controller.resolutions == 0, "JAVA_T2_HOOK_EMPTY_NO_RESOLVE");
            require("legacy".equals(name) ? DraftParityHooks.faultDefects().isEmpty() : defect("FAULT_BIND_EMPTY_STACK_TRACE"), "legacy".equals(name) ? "JAVA_T1_FAULT_CHANNEL_FREEZE" : "JAVA_T2_HOOK_EMPTY_DEFECT");
        } else if ("rebind".equals(name)) {
            DraftParityHooks.behaviorMissing(a, "First", "one"); DraftParityHooks.behaviorMissing(a, "Second", "two");
            require("First".equals(operands(arm(null, a)).get("imageSet")) && defect("FAULT_BIND_ALREADY_BOUND"), "JAVA_T2_HOOK_REBIND");
        } else if ("label".equals(name)) {
            DraftParityHooks.behaviorMissing(a, "Member", "Name"); DraftParityHooks.arm("not-a-wrapper", a);
            require(DraftParityHooks.takeArmedFault() == null && defect("FAULT_ARM_LABEL") && unknown(arm("birth", a)), "JAVA_T2_HOOK_LABEL_CONSUMED");
        } else if ("resolver-failure".equals(name)) {
            controller.refused = true;
            require(DraftParityHooks.behaviorMissing(a, "Member", "Name") == a && unknown(arm("birth", a)) && defect("FAULT_BIND_FAILED"), "JAVA_T2_HOOK_RESOLVER_FAILURE");
        } else if ("guards".equals(name)) {
            DraftParityHooks.nextBehaviorMissing(a, "Member", "Name", new Object()); DraftParityHooks.scanTargetMissing(a, "ref", new Object());
            require(unknown(arm("arrive", a)) && controller.resolutions == 0, "JAVA_T2_HOOK_OPERAND_GUARDS");
            DraftParityHooks.nextBehaviorMissing(a, "Member", null, null); require("behavior-missing".equals(arm("arrive", a).get("code")), "JAVA_T2_HOOK_OPERAND_GUARDS");
            DraftParityHooks.scanTargetMissing(a, "ref", null); require("scan-target-missing".equals(arm("arrive", a).get("code")), "JAVA_T2_HOOK_OPERAND_GUARDS");
            DraftParityHooks.parameterInvalid(a, "ref", "BornInterval"); require("BornInterval".equals(operands(arm(null, a)).get("parameter")), "JAVA_T2_HOOK_OPERAND_GUARDS");
        } else throw new IllegalArgumentException(name);
    }

    @SuppressWarnings("unchecked")
    public static void main(String[] args) throws Exception {
        if (args.length == 2 && "hooks".equals(args[0])) { hooks(args[1]); System.out.println("TRACE2_HOOK_PROBE_EXECUTED: " + args[1]); return; }
        Map<String, Object> lookup = new LinkedHashMap<String, Object>();
        lookup.put("imageSet", "SearchedMember");
        lookup.put("behavior", null);
        List<String> via = new ArrayList<String>();
        via.add("birth");
        DraftParityHooks.Fault fault = new DraftParityHooks.Fault("behavior-missing", lookup, via);
        lookup.put("behavior", "changed-after-capture");
        via.add("transform");
        Map<String, Object> first = DraftParityHooks.faultRecord(fault);
        require(((Map<String, Object>)first.get("lookup")).get("behavior") == null, "JAVA_T2_IMMUTABLE_CAPTURE");
        require(((List<String>)first.get("via")).size() == 1, "JAVA_T2_IMMUTABLE_VIA");
        ((Map<String, Object>)first.get("lookup")).put("imageSet", "changed-after-write");
        ((List<String>)first.get("via")).clear();
        Map<String, Object> second = DraftParityHooks.faultRecord(fault);
        require("SearchedMember".equals(((Map<String, Object>)second.get("lookup")).get("imageSet")),
                "JAVA_T2_IMMUTABLE_LOOKUP");
        require(((List<String>)second.get("via")).size() == 1, "JAVA_T2_IMMUTABLE_WRITER_VIA");
        Map<String, Object> absent = DraftParityHooks.faultRecord(null);
        require("unclassified".equals(absent.get("code")) && absent.get("lookup") == null
                && ((List<String>)absent.get("via")).isEmpty(), "JAVA_T2_UNCLASSIFIED_FALLBACK");
        System.out.println("TRACE2_PROTOCOL_PROBE_EXECUTED: immutable stored fault and absent-channel controls");
    }
}
