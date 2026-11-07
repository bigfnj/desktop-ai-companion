"""Synthetic Java-8 proof of the event digest transport, never the upstream runtime."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import time

PROBE = r'''import com.group_finity.mascot.parity.DraftTraceWriter;
import java.io.StringWriter;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.LinkedHashMap;
import java.util.Map;
public final class EventDigestProbe {
    static Map<String, Object> record(String kind, String value) {
        Map<String, Object> r = new LinkedHashMap<String, Object>();
        r.put("type", "event"); r.put("tick", 1); r.put("sid", "m1");
        r.put("kind", kind); r.put("value", value); return r;
    }
    static void require(boolean value, String label) {
        if (!value) throw new IllegalStateException(label);
        System.out.println("PASS " + label);
    }
    static String sha(byte[] data) throws Exception {
        StringBuilder text = new StringBuilder();
        for (byte b : MessageDigest.getInstance("SHA-256").digest(data))
            text.append(String.format("%02x", b & 255));
        return text.toString();
    }
    public static void main(String[] args) throws Exception {
        Map<String, Object> a = record("drag", "start\n\u03c0");
        Map<String, Object> b = record("behavior", "Dragged");
        StringWriter written = new StringWriter();
        DraftTraceWriter writer = new DraftTraceWriter(written);
        writer.writeRecord(a); writer.writeRecord(b);
        DraftTraceWriter.EventSequence sequence = new DraftTraceWriter.EventSequence();
        sequence.add(a); sequence.add(b);
        String expected = sha(written.toString().getBytes(StandardCharsets.UTF_8));
        require(sequence.finish().equals(expected), "EVENT_SEQUENCE_WRITTEN_BYTES");
        require(sequence.finish().equals(expected), "EVENT_SEQUENCE_FINISH_STABLE");
        DraftTraceWriter.EventSequence reverse = new DraftTraceWriter.EventSequence();
        reverse.add(b); reverse.add(a);
        require(!reverse.finish().equals(expected), "EVENT_SEQUENCE_REORDERED_SAME_TOTAL");
        DraftTraceWriter.EventSequence changed = new DraftTraceWriter.EventSequence();
        changed.add(a); changed.add(record("behavior", "Thrown"));
        require(!changed.finish().equals(expected), "EVENT_SEQUENCE_CHANGED_SAME_TOTAL");
        require(new DraftTraceWriter.EventSequence().finish().equals(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"), "EVENT_SEQUENCE_EMPTY");
        boolean refused = false;
        try { sequence.add(a); } catch (IllegalStateException correct) { refused = true; }
        require(refused, "EVENT_SEQUENCE_FINISHED_REFUSES_APPEND");
    }
}
'''

def main():
    p = argparse.ArgumentParser()
    p.add_argument("--jdk-root", type=Path, required=True)
    p.add_argument("--repo", type=Path, required=True)
    p.add_argument("--temp-root", type=Path, required=True)
    a = p.parse_args()
    source = a.repo / "tools/ShimejiParity/oracle/draft/DraftTraceWriter.java"
    original = source.read_bytes()
    needle = 'digest.update((recordJson(record) + "\\n").getBytes(StandardCharsets.UTF_8));'
    text = original.decode("utf-8")
    assert text.count(needle) == 1, "mutation pattern not unique"
    version = subprocess.run([str(a.jdk_root / "bin/java.exe"), "-version"], capture_output=True, text=True, timeout=15)
    assert version.returncode == 0 and 'version "1.8.0_' in version.stderr, "EVENT_DIGEST_REQUIRES_JDK8"
    results = []
    with tempfile.TemporaryDirectory(prefix="event-digest-", dir=a.temp_root) as private:
        private_root = Path(private)
        for name, body in (("fixed", text), ("drops-events", text.replace(needle, "/* mutation: omit event */"))):
            case = Path(private) / name
            case.mkdir()
            candidate, probe = case / "DraftTraceWriter.java", case / "EventDigestProbe.java"
            candidate.write_text(body, encoding="utf-8", newline="\n")
            probe.write_text(PROBE, encoding="utf-8", newline="\n")
            edited = max(candidate.stat().st_mtime_ns, probe.stat().st_mtime_ns)
            time.sleep(0.03)
            build = subprocess.run([str(a.jdk_root / "bin/javac.exe"), "-encoding", "UTF-8", "-d", str(case),
                                    str(candidate), str(probe)], capture_output=True, text=True, timeout=30)
            assert build.returncode == 0, build.stderr
            artifact = case / "com/group_finity/mascot/parity/DraftTraceWriter$EventSequence.class"
            assert artifact.is_file() and artifact.stat().st_mtime_ns > edited, "rebuild timestamp did not advance"
            artifact_sha = hashlib.sha256(artifact.read_bytes()).hexdigest()
            print("REBUILT", name, "mtime advanced", artifact_sha, flush=True)
            run = subprocess.run([str(a.jdk_root / "bin/java.exe"), "-Djava.awt.headless=true", "-cp", str(case),
                                  "EventDigestProbe"], capture_output=True, text=True, timeout=30)
            if name == "fixed":
                assert run.returncode == 0 and run.stdout.count("PASS EVENT_SEQUENCE_") == 6, run.stderr + run.stdout
                status = "PASS"
            else:
                assert artifact_sha != results[0]["artifactSha256"], "EVENT_DIGEST_MUTANT_ARTIFACT_UNCHANGED"
                assert run.returncode != 0 and run.stderr.count("EVENT_SEQUENCE_WRITTEN_BYTES") == 1, run.stderr + run.stdout
                status = "FIRED"
            results.append({"case": name, "status": status, "artifactSha256": artifact_sha,
                            "rebuildMtimeAdvanced": True, "exitCode": run.returncode})
            print(status, name, flush=True)
    assert source.read_bytes() == original, "tracked source changed"
    assert not private_root.exists(), "EVENT_DIGEST_PRIVATE_BUILDS_REMAIN"
    result = {"results": results, "trackedSourceUnchanged": True, "privateBuildsRemoved": not private_root.exists()}
    (a.temp_root / "event-digest-results.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result))

if __name__ == "__main__":
    main()
