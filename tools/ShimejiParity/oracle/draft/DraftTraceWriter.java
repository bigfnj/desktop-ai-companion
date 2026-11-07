package com.group_finity.mascot.parity;

import java.io.Closeable;
import java.io.IOException;
import java.io.StringWriter;
import java.io.Writer;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.Map;

/** JSONL transport only. No contract field names are frozen by this class. */
public final class DraftTraceWriter implements Closeable {
    private final Writer writer;
    public DraftTraceWriter(Writer writer) { this.writer = writer; }

    public synchronized void writeRecord(Map<String, Object> record) throws IOException {
        writer.write(recordJson(record) + "\n");
        writer.flush();
    }

    /** The same bytes back both the trace and its trace-on/off event witness. */
    private static String recordJson(Map<String, Object> record) throws IOException {
        StringWriter buffer = new StringWriter();
        new DraftTraceWriter(buffer).value(record);
        return buffer.toString();
    }

    /**
     * N-shimeji-1106-21: counts cannot distinguish reordered or changed events. Feed records in their final output
     * order, including the input events inserted ahead of callback-generated behavior events, in both observing modes.
     * Only stored values are serialized; no source variable, script or condition is evaluated to make this witness.
     */
    public static final class EventSequence {
        private final MessageDigest digest;
        private String finished;

        public EventSequence() {
            try { digest = MessageDigest.getInstance("SHA-256"); }
            catch (NoSuchAlgorithmException failure) { throw new IllegalStateException("SHA-256 unavailable", failure); }
        }

        public void add(Map<String, Object> record) throws IOException {
            if (finished != null) throw new IllegalStateException("event sequence already finished");
            digest.update((recordJson(record) + "\n").getBytes(StandardCharsets.UTF_8));
        }

        public String finish() {
            if (finished == null) {
                StringBuilder hex = new StringBuilder(64);
                for (byte value : digest.digest()) {
                    int unsigned = value & 255;
                    hex.append(Character.forDigit(unsigned >>> 4, 16)).append(Character.forDigit(unsigned & 15, 16));
                }
                finished = hex.toString();
            }
            return finished;
        }
    }

    private void quoted(String text) throws IOException {
        writer.write('"');
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            if (c == '"' || c == '\\') writer.write('\\');
            if (c < 32) writer.write(String.format("\\u%04x", (int)c)); else writer.write(c);
        }
        writer.write('"');
    }

    private void value(Object item) throws IOException {
        if (item == null) { writer.write("null"); return; }
        if (item instanceof String) { quoted((String)item); return; }
        if (item instanceof Boolean) { writer.write(item.toString()); return; }
        if (item instanceof Number) {
            double number = ((Number)item).doubleValue();
            if (Double.isNaN(number) || Double.isInfinite(number)) throw new IOException("non-finite-trace-number");
            writer.write(item.toString()); return;
        }
        if (item instanceof Map) {
            writer.write('{');
            boolean first = true;
            for (Object entryObject : ((Map<?, ?>)item).entrySet()) {
                Map.Entry<?, ?> entry = (Map.Entry<?, ?>)entryObject;
                if (!(entry.getKey() instanceof String)) throw new IOException("non-string-trace-key");
                if (!first) writer.write(',');
                first = false;
                quoted((String)entry.getKey()); writer.write(':'); value(entry.getValue());
            }
            writer.write('}'); return;
        }
        if (item instanceof Iterable) {
            writer.write('['); boolean first = true;
            for (Object member : (Iterable<?>)item) { if (!first) writer.write(','); first = false; value(member); }
            writer.write(']'); return;
        }
        throw new IOException("unsupported-trace-value " + item.getClass().getName());
    }

    public void close() throws IOException { writer.close(); }
}
