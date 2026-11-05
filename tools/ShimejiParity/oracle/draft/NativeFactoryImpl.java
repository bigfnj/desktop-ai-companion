package com.group_finity.mascot.parity;

import com.group_finity.mascot.NativeFactory;
import com.group_finity.mascot.environment.Environment;
import com.group_finity.mascot.image.NativeImage;
import com.group_finity.mascot.image.TranslucentWindow;
import com.group_finity.mascot.exception.OutOfMemoryException;
import java.awt.image.BufferedImage;

public final class NativeFactoryImpl extends NativeFactory {
    private final com.group_finity.mascot.virtual.NativeFactoryImpl display = new com.group_finity.mascot.virtual.NativeFactoryImpl();
    private final ScenarioEnvironment environment = new ScenarioEnvironment(display.getEnvironment());
    public Environment getEnvironment() { return environment; }
    public NativeImage newNativeImage(BufferedImage image, boolean mirror) throws OutOfMemoryException {
        return display.newNativeImage(image, mirror);
    }
    public TranslucentWindow newTransparentWindow() { return display.newTransparentWindow(); }
}
