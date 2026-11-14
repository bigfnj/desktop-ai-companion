using System.Collections.Generic;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The instant and one-shot embedded classes (grimoire/06 section 5): Look, Offset, Mute, BringToFront, SelfDestruct.
    /// The four instants apply at init when their own condition holds and never step (InstantAction); SelfDestruct is
    /// an Animate that disposes the companion on its penultimate tick.
    /// </summary>
    internal static class TickInstantFamily
    {
        public static TickAction TryCreate(TickWorld world, TickType type, TickActDef def, Dictionary<string, TickParamDef> incoming)
        {
            switch (def.Class)
            {
                case "Look": return new TickLook(world, def, incoming);
                case "Offset": return new TickOffset(world, def, incoming);
                case "Mute": return new TickMute(world, def, incoming);
                case "BringToFront": return new TickBringToFront(world, def, incoming);
                case "SelfDestruct": return new TickSelfDestruct(world, def, incoming);
                default: return null;
            }
        }
    }

    /// <summary>Look: facing set from LookRight, which defaults to the opposite of the current facing (Look.java).</summary>
    internal sealed class TickLook : TickInstantAction
    {
        public TickLook(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        protected override void Apply()
        {
            // The default is computed before the parameter is read, as Java evaluates eval's arguments first.
            bool opposite = !Mascot.LookRight;
            Mascot.LookRight = EvalBool("LookRight", null, opposite);
        }
    }

    /// <summary>Offset: the anchor moves by (X, Y) at once, X read before Y (Offset.java).</summary>
    internal sealed class TickOffset : TickInstantAction
    {
        public TickOffset(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        protected override void Apply()
        {
            int x = EvalInt("X", 0);
            int y = EvalInt("Y", 0);
            Mascot.Anchor = new TickPoint(Mascot.Anchor.X + x, Mascot.Anchor.Y + y);
        }
    }

    /// <summary>
    /// Mute (Mute.java): a named Sound stops the matching clips, which may be other companions' (the source's clip
    /// cache is shared); with no Sound, and this companion's sounds on, every cached clip stops. The engine reports
    /// it, then stops the clips through the world's clip output (<see cref="TickSound"/>); a world with none (a parity
    /// run) only traces it.
    /// </summary>
    internal sealed class TickMute : TickInstantAction
    {
        public TickMute(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        protected override void Apply()
        {
            // CONTRACT v1.4 P2-A and P2-E: Sound is read once, and EvalString's passive record says which definition that
            // read selected, the result's kind before display coercion and whether a fault was reported during it. Every
            // completed read reaches the observer exactly once with that record: through Muted on the two branches that
            // report a `mute` (each still before its TickSound call), or through MuteReadNoEvent on the null-with-Sounds-off
            // branch, which reports nothing. No second read, no second settings read, and TickSound receives the same
            // argument as before; TickHost's observer stays a no-op.
            TickParamRead read;
            string sound = EvalString("Sound", null, null, out read);
            if (sound != null)
            {
                World.Observer.Muted(Mascot, sound, this, read);
                TickSound.MuteNamed(World, Mascot, sound);
            }
            else if (World.Settings.Get("Sounds", Mascot.Type.Name))
            {
                World.Observer.Muted(Mascot, null, this, read);
                TickSound.MuteAll(World, Mascot);
            }
            else World.Observer.MuteReadNoEvent(Mascot, this, read);
        }
    }

    /// <summary>BringToFront: raises this companion's window (Mascot.bringToFront). The window layer acts on the
    /// request; headless, nothing is drawn, so nothing changes.</summary>
    internal sealed class TickBringToFront : TickInstantAction
    {
        public TickBringToFront(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        protected override void Apply()
        {
            Mascot.BringToFrontRequested = true;
        }
    }

    /// <summary>SelfDestruct: an Animate that disposes the companion on its penultimate tick, or its only one
    /// (SelfDestruct.java). The saved companion mix is untouched, so it returns at the next launch.</summary>
    internal sealed class TickSelfDestruct : TickAnimate
    {
        public TickSelfDestruct(TickWorld world, TickActDef def, Dictionary<string, TickParamDef> incoming) : base(world, def, incoming) { }

        protected override void Tick()
        {
            base.Tick();
            int duration = AnimationDuration();
            if (Time == duration - 1 || duration == 1) Mascot.Dispose("self-destruct");
        }
    }
}
