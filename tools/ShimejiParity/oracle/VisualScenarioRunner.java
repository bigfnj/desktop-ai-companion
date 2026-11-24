package com.group_finity.mascot.parity;

/** Entry point included only in the ungraded visual-observation runner JAR. */
public final class VisualScenarioRunner {
    private VisualScenarioRunner() { }

    public static void main(String[] args) {
        V1ScenarioRunner.runMain(args, true);
    }
}
