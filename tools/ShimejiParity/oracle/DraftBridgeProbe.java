package com.group_finity.mascot.parity;

import com.group_finity.mascot.Mascot;
import com.group_finity.mascot.script.Script;
import com.group_finity.mascot.script.Variable;
import com.group_finity.mascot.script.VariableMap;
import java.awt.Point;
import java.awt.Rectangle;
import java.io.IOException;
import java.io.StringWriter;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Exercises the source Script implementation with the draft bridge installed. */
public final class DraftBridgeProbe {
    private static final class Controller implements DraftParityHooks.Controller {
        private final double[] tape = { 0.25, 0.5, 0.75, 0.125 };
        private int draws;
        public void beforeStep() { }
        public void afterStep(List<Mascot> a, List<Mascot> b, List<Mascot> c) { }
        public void observe(String hook, Object subject, Object detail) { }
        public double random(String site) {
            if (draws == tape.length) throw new IllegalStateException("rng-tape-exhausted");
            return tape[draws++];
        }
        public boolean start(String imageSet) { return false; }
        public DraftParityHooks.Snapshot snapshot() {
            return new DraftParityHooks.Snapshot(new Rectangle(0, 0, 640, 480), new Rectangle(0, 0, 640, 480),
                new Point(100, 100), new Rectangle(), false, false, "");
        }
    }

    private static final class Unused extends Variable {
        private int reads;
        public void init() { }
        public void initFrame() { }
        public Object get(VariableMap variables) { reads++; return 99; }
    }

    private static void require(boolean value, String label) {
        if (!value) throw new AssertionError("DraftBridgeProbe:" + label);
    }

    public static void main(String[] args) throws Exception {
        Controller controller = new Controller();
        DraftParityHooks.install(controller);
        VariableMap plain = new VariableMap();
        Unused off = new Unused();
        plain.put("unusedWitness", off);
        System.setProperty("shimeji.parity.enabled", "false");
        require(((Number)new Script("1+2", false).get(plain)).intValue() == 3, "source-script-baseline");
        VariableMap controlled = new VariableMap();
        Unused on = new Unused();
        controlled.put("unusedWitness", on);
        System.setProperty("shimeji.parity.enabled", "true");
        require(((Number)new Script("1+2", false).get(controlled)).intValue() == 3, "source-script-bridged");
        require(on.reads == off.reads, "no-extra-lazy-variable-evaluation");
        Script once = new Script("Math.random()", false);
        once.init();
        require(((Number)once.get(controlled)).doubleValue() == 0.25, "first-script-draw");
        require(((Number)once.get(controlled)).doubleValue() == 0.25 && controller.draws == 1, "action-cache");
        once.initFrame();
        require(((Number)once.get(controlled)).doubleValue() == 0.25 && controller.draws == 1, "action-cache-survives-frame");
        once.init();
        require(((Number)once.get(controlled)).doubleValue() == 0.5, "action-cache-resets-entry");
        Script frame = new Script("Math.random()", true);
        frame.init();
        require(((Number)frame.get(controlled)).doubleValue() == 0.75, "frame-first-draw");
        require(((Number)frame.get(controlled)).doubleValue() == 0.75 && controller.draws == 3, "frame-cache");
        frame.initFrame();
        require(((Number)frame.get(controlled)).doubleValue() == 0.125 && controller.draws == 4, "frame-cache-resets");
        StringWriter buffer = new StringWriter();
        DraftTraceWriter writer = new DraftTraceWriter(buffer);
        Map<String, Object> record = new LinkedHashMap<String, Object>();
        record.put("witness", "quote\" slash\\ newline\n tab\t");
        writer.writeRecord(record);
        String valid = buffer.toString();
        boolean refused = false;
        try { record.put("number", Double.NaN); writer.writeRecord(record); }
        catch (IOException expected) { refused = expected.getMessage().equals("non-finite-trace-number"); }
        require(refused, "trace-non-finite-refused");
        require(buffer.toString().equals(valid), "trace-invalid-record-is-atomic");
        System.out.println("{\"status\":\"pass\",\"scriptDraws\":" + controller.draws
            + ",\"unusedReadsOff\":" + off.reads + ",\"unusedReadsOn\":" + on.reads + "}");
    }
}
