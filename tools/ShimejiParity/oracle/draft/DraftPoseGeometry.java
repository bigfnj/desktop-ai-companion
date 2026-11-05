package com.group_finity.mascot.parity;

import com.group_finity.mascot.Mascot;
import com.group_finity.mascot.image.MascotImage;
import java.awt.Point;
import java.awt.Rectangle;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.Map;

/** Observation only. The future protocol adapter supplies applied-pose presence. */
public final class DraftPoseGeometry {
    private DraftPoseGeometry() { }

    public static Map<String, Object> observe(Mascot mascot, boolean appliedPose) {
        Map<String, Object> result = new LinkedHashMap<String, Object>();
        MascotImage image = mascot.getImage();
        Rectangle bounds = appliedPose ? mascot.getBounds() : null;
        Point center = !appliedPose || image == null ? null : image.getCenter();
        result.put("origin", bounds == null ? null : Arrays.asList(bounds.x, bounds.y));
        result.put("poseAnchor", center == null ? null : Arrays.asList(center.x, center.y));
        return result;
    }
}
