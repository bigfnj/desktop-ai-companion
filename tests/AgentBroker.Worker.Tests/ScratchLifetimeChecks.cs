using AgentBroker.TestSupport;

internal static class ScratchLifetimeChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: POSTPUSH_SCRATCH_LIFETIME_EXECUTED");
        string root;
        using (var owned = OwnedScratch.Create("lifetime"))
        {
            root = owned.Root;
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            File.WriteAllText(Path.Combine(root, "nested", "synthetic.txt"), "Synthetic fixture bytes.");
        }
        check(!Path.Exists(root), "SCRATCH_OWNED_REMOVED: disposal removes and verifies the exact created tree");
        var fixture = new ToolFixture("fixture-lifetime");
        string fixtureRoot = fixture.Root;
        fixture.Register("Synthetic cleanup witness");
        await fixture.DisposeAsync();
        check(!Path.Exists(fixtureRoot), "SCRATCH_FIXTURE_WIRING: the real shared fixture closes the store and removes its root");

        using (var audit = new ScratchAudit())
        {
            var leaked = OwnedScratch.Create("audit-witness");
            bool refused = false;
            try { audit.Dispose(); }
            catch (IOException error) when (error.Message.StartsWith("SCRATCH_INVOCATION_CLEAN", StringComparison.Ordinal)) { refused = true; }
            finally { leaked.Dispose(); }
            check(refused, "SCRATCH_AUDIT_FIRES: a newly leaked invocation root fails the actual cleanup audit");
        }

        using var target = OwnedScratch.Create("link-target");
        using var linked = OwnedScratch.Create("link-root");
        string sentinel = Path.Combine(target.Root, "synthetic-sentinel.txt");
        File.WriteAllText(sentinel, "Synthetic sentinel.");
        string link = Path.Combine(linked.Root, "linked-child");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = "/d /c mklink /J \"" + link + "\" \"" + target.Root + "\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        using (var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Synthetic junction did not start."))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0) throw new IOException("Synthetic junction creation failed.");
        }
        bool linkedRefused = false;
        try { linked.Dispose(); }
        catch (IOException error) when (error.Message.Contains("linked", StringComparison.Ordinal)) { linkedRefused = true; }
        finally
        {
            // Remove this exact created link itself, without recursive traversal, before retrying its owned parent.
            if (Directory.Exists(link)) Directory.Delete(link);
            linked.Dispose();
        }
        check(linkedRefused && File.ReadAllText(sentinel) == "Synthetic sentinel.",
            "SCRATCH_LINK_REFUSED: cleanup refuses a linked child before touching the separate owned target");
    }
}
