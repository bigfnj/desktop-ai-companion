using System;
using System.Collections.Generic;

namespace DesktopAICompanion.Wpf
{
    /// <summary>
    /// One short, unique, tongue-in-cheek blurb per bundled pet, keyed by catalog id (S5b-2c3). The seven
    /// colored sheep share one 268-move animation set, so each gets its own colour-based quip to keep the
    /// descriptions distinct. Purely cosmetic copy for the Pets gallery; an unknown id falls back to a
    /// generic line.
    /// </summary>
    internal static class CompanionBlurbs
    {
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "eSheep",        "The original parkour sheep by Adriano. Accept no substitutes." },
            { "blue_sheep",    "Ben — blue as a Monday and twice as athletic; parkours to avoid his feelings." },
            { "green_sheep",   "Gus — green and serene; never once jealous, since he's already the best climber." },
            { "orange_sheep",  "Omar — orange you glad he's here? 268 moves, every one a showoff." },
            { "pink_sheep",    "Pearl — pretty in pink and extra bouncy; quietly judging your desktop clutter." },
            { "purple_sheep",  "Patsu — purple and faintly regal; convinced he is desktop royalty." },
            { "red_sheep",     "Rick — red-hot and always rolling; never gonna give your cursor up." },
            { "yellow_sheep",  "Yogurt — mellow yellow with a rocket habit; may spontaneously blast off." },
            { "esheep64",      "The 64-move remix — fewer tricks, same swagger." },
            { "bbunny",        "A bunny. It hops, it judges, it hops again." },
            { "blue_ham_ham",  "A tiny blue hamster with enormous cheek energy." },
            { "fox",           "Sly, quick, and about 27 flavors of mischief." },
            { "mareep",        "An electric sheep that dreams of androids." },
            { "mimiko",        "A small black cat with a large list of opinions." },
            { "negima",        "Pure anime energy, rendered in pixels." },
            { "neko",          "The internet's favorite cat, now loitering on your taskbar." },
            { "pikachu",       "You knew this one was coming. Pika." },
            { "pingus",        "A penguin with the most moves outside the sheep club (28!); waddles with purpose." },
            { "pink_fox",      "Fox, but make it pink — same cunning, more flair." },
            { "pink_neko",     "Neko in its full Barbie era." },
            { "shiny_sylveon", "Sparkly, elegant, and well aware of it — the shiny-variant flex." },
            { "ssj-goku",      "Powering up on your desktop. It's over 9000 pixels." },
            { "yellow_neko",   "A sunny cat that pairs with any wallpaper." },

            // Converted Shimeji skins. Without an entry here every one of them fell back to the generic
            // "A delightful desktop companion", which made 29 of the 51 catalog pets look interchangeable.
            // Keyed by catalog id, same as above; the stats line (animations / sounds) is computed from the
            // pet itself and already worked.
            { "shimeji-06n2wuu6",       "Kuromi — Sanrio's resident punk. All skulls and mischief, secretly a softie." },
            { "shimeji-08dkbwmb",       "Cammy White — Street Fighter's finest, ready to cannon-spike your taskbar." },
            { "shimeji-1l2yvz73",       "Frieza — still insists you address him properly. Several forms, no patience." },
            { "shimeji-36po5aw2",       "Bugcat Capoo — a small blue cat-bug of pure chaos and zero regrets." },
            { "shimeji-3g8t9v4e",       "Koro — keeping a close eye on things from the corner of your screen." },
            { "shimeji-3x56f4pl",       "Monkey D. Luffy — rubber-limbed, straw-hatted, permanently hungry." },
            { "shimeji-55atqs1b",       "Hikari — bringing a little light to the bottom of your screen." },
            { "shimeji-5xs0ld2m",       "Nightmare Sans — broods magnificently, judges silently, never blinks." },
            { "shimeji-76xviks0",       "ADAM — forty animations of quiet, unhurried menace." },
            { "shimeji-7gb3ediv",       "Nezuko — bamboo optional, determination very much not." },
            { "shimeji-88f9sqb5",       "A Halloween skeleton who never got the memo that October ended." },
            { "shimeji-8opqq9of",       "Lute — drifting about with forty moves and no particular agenda." },
            { "shimeji-8u2lojrb",       "Skitty — chases its own tail with genuine, unwavering commitment." },
            { "shimeji-8vqm59ot",       "Shiny Wooper — the same blank stare, in a considerably rarer palette." },
            { "shimeji-9imr7z1s",       "Wooper — has no idea what is going on, and that is entirely the appeal." },
            { "shimeji-9qc0h184",       "Vlad — brooding on your taskbar as though it were a Carpathian balcony." },
            { "shimeji-alipheese-fateburn-xvi", "Alipheese Fateburn XVI — monster-girl royalty, and she has the name to prove it." },
            { "shimeji-brq51bkr",       "A quiet, watchful presence at the edge of your desktop." },
            { "shimeji-dqjd9s2d",       "SpongeBob — absorbent, yellow and porous. Now also on your taskbar." },
            { "shimeji-gengar",         "Gengar — a Ghost-type lurking at the screen's edge. It has sounds, and it uses them." },
            { "shimeji-hornet-9b9d1d",  "Hornet — climbs walls, throws her nail, and is visibly unimpressed by your bugs." },
            { "shimeji-loona-hellhound", "Loona — hellhound receptionist energy: mostly unimpressed, occasionally loud." },
            { "shimeji-rick-and-morty-rick-by-starrii-chan", "Rick Sanchez — burping his way across your desktop, unbothered." },
            { "shimeji-capybara-albino", "Capybara — the world's most relaxed rodent, doing absolutely nothing with total confidence." },
            { "shimeji-cyn",            "Cyn — unsettlingly cheerful for something with that many teeth. Do not make eye contact." },
            { "shimeji-kinitopet",      "KinitoPET — your friendly desktop assistant! He would very much like to stay. Please let him stay." },
            { "shimeji-ralsei",         "Ralsei — a very polite fluffy prince who would like you to know he baked something." },
            { "shimeji-serial-designation-j", "Serial Designation J — ruthlessly efficient, faintly terrifying, and the best climber in the catalog." },
            { "shimeji-south-park-cartman", "Cartman — demands your authoritah, respects none of your deadlines." },
            { "shimeji-uzi-doorman-ef5c7d", "Uzi Doorman — sixty-six animations of concentrated teenage rebellion." },
            { "shimeji-vocaloid-gakupo", "Gakupo — Vocaloid's samurai, striking a pose whether or not there is a song." },
        };

        public static string For(string id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                string blurb;
                if (Map.TryGetValue(id, out blurb)) return blurb;
            }
            return "A delightful desktop companion.";
        }
    }
}
