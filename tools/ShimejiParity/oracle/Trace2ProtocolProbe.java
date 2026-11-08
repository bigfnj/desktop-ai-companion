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

    @SuppressWarnings("unchecked")
    public static void main(String[] args) {
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
