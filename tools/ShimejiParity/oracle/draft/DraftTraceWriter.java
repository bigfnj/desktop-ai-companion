package com.group_finity.mascot.parity;

import java.io.Closeable;
import java.io.IOException;
import java.io.StringWriter;
import java.io.Writer;
import java.util.Map;

/** JSONL transport only. No contract field names are frozen by this class. */
public final class DraftTraceWriter implements Closeable {
    private final Writer writer;
    public DraftTraceWriter(Writer writer) { this.writer = writer; }

    public synchronized void writeRecord(Map<String, Object> record) throws IOException {
        StringWriter buffer = new StringWriter();
        new DraftTraceWriter(buffer).value(record);
        writer.write(buffer.toString() + "\n");
        writer.flush();
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
