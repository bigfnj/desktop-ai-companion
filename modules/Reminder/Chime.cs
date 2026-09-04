using System;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.ReminderModule
{
    /// <summary>
    /// Plays a short, pleasant two-note chime through the host's SHARED audio output the instant a
    /// reminder is announced, so an off-screen user gets an audible nudge, not just a speech bubble.
    ///
    /// The clip is a tiny (~0.75 s, mono, 44.1 kHz) MP3 embedded as a base64 constant, so the module
    /// needs no asset file, no resource and no .csproj change. Its bytes begin with an "ID3" tag, which
    /// the host's MP3 sniff (Mp3Format.LooksLikeMp3 / AudioOutput.DecodeModuleAudio) accepts.
    ///
    /// Audio is a nicety, never load-bearing: every failure path is swallowed. If the host refuses
    /// (this module hasn't declared <see cref="ModulePermissions.Audio"/>, host predates 1.6.0, the box
    /// has no device, or the user is muted) <see cref="IHost.PlaySound"/> simply returns false and the
    /// reminder's spoken bubble still fires. Play never throws into the caller's tick.
    /// </summary>
    internal static class Chime
    {
        // ffmpeg-generated: two sine tones (G5 784 Hz -> C6 1047 Hz) with short in/out fades, mono,
        // 44.1 kHz, 64 kbps MP3. Kept small (a few KB) precisely so it can live here as a literal.
        private const string ChimeMp3Base64 =
            "SUQzBAAAAAAAI1RTU0UAAAAPAAADTGF2ZjYyLjEyLjEwMQAAAAAAAAAAAAAA//tQwAAAAAAAAAAAAAAAAAAAAAAASW5mbwAAAA8AAAAeAAAZTQ" +
            "AQEBAYGBggICApKSkpMTExOTk5QkJCQkpKSlJSUlpaWlpjY2Nra2tzc3Nze3t7hISEjIyMjJSUlJycnKWlpaWtra21tbW9vb29xsbGzs7O1tbW" +
            "1t7e3ufn5+/v7+/39/f///8AAAAATGF2YzYyLjI4AAAAAAAAAAAAAAAAJAONAAAAAAAAGU0dXFDbAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAP/7UMQAAwkEQQRNcMcBLIVhzc9pAAOvuvpc8wBQ6DBp9ZrcxjZakacO/6k2eQjPAoQ5GEgA3QuxLFjw+/8bAZMmTJkEIMgEAQB8H/iD" +
            "if6j//f6nFA/9P2/+f//y5YhTSVNDtpiLjGBAKYbDBuyGGsmycGfxr+LCGEUE6cmUYwmYsUkKoFd7DvQVBV0s7Wv1bA9CxECb/3dW9/YIP2a3u" +
            "/sa/+fENv7epVmhbcwECjBYdMLDoxaeTM0iOz78wvcCSMFqOojfRhl8wlQDkOVPTP/+1LEE4POEGkEDn9mQU4F4MGf7JBgoMJxoETPXpAlNBMF" +
            "QDDUZjU7RUdiVzlPSV7ly7cxyxy53vM8N1L4jLWgJQs1k08l5fmq79RI8ld0re6l1qlHbPRnv1bFZ7/Y1lq1kkQwo1gz9kMCeAjzCpQEkxBUIq" +
            "MOBOMjULTRkwrMISPw5ze340RJMjJjCA1W5xVdOra3FiPr2V3trndT2pX99K7qOz+eT2p3Mq/o/exCv9frYSIgAAiZhhQZGpmlVRgU4NWYS4CD" +
            "GECAURiXZSedNf/7UsQPggvkKwIN/4SBcoWgod/wyKThGKBglp4YsGuw+ZmBBj0BmIgEYPApZ9TCGwjpVy22hfW96N9vQtb6Sj3kV8juYncRX6" +
            "ryFX3sTu9qdlX3uqWAUBLqT5GgCFgZBQlGKQMmZI8Hmg3GJlBN5hzCHGaWofNGFOBOpvGIGfkuZHLBiQYmCQknqzZPltrJKu1CrvYhVyUXUquZ" +
            "5Ouyqzvo2UqezulLrPRnKehfXt7mJ7VZQqAwQEjBIpMOFIx6rTRm2P4vswT0EJMUaH+DrSh5//tSxA+Dy8ArAg5/hkGqpCAB/glQ8xUADKPQic" +
            "DZAOc4YiAMTzCACLUKrwALucjsTtSv1Peit7RVW1a9Be4gjp7UbiK7m1zqKtO6vdXcRQueq9XrStHgAIoAGSgA7HgHkwH0AEMEcAWTEYQLsxlc" +
            "MYMQVWlDSTVOUwrUNIOEW80izDKh9MWkowYJSECKtQOXrPK1PT+T08m9d4LJRurVx/10r9pX67/q/v7+eg8n96468jvpXPU9itqM/3oXuJIAAA" +
            "oAZCm4IQOCAwYJHZhxAGT/+1LECQIKjC0HDn+GQYYtoMntiVDaicYVhgEgIQYhKIFnEZh+RiFYB2NpUInwkrSgxCQwBwPR7ZhQ6Qu96F2/TuYv" +
            "oVt9vYld2vq6r/0L0/3UdPp/tT0KjQTTlOESxYBgOBHDgoB4YIzAgijREH5MkD4E3eOXDE8I1N/7jRIEyNKMMKQaIJvtDWa81636+ub09fT7U2" +
            "8a+36vNVveZqV1/X1b29d7eu1Na/f/t7+batXg7ko39wnR0QAACgNkSf4oBgqGTAA4MIn8xhUDVafMCv/7UsQMAgtcKwcOf4ZBRgVhpZ9skOBR" +
            "TEVAWw4qAGbMRAAHzqoeNSA8y6AA40kQ3CAYlQ0ePHXaL6NtV3c1FTm1evSu76NzU7Ep/PE99HYU3VXGar6PV9wDbrLUVb9XgkGEOAN8wfAFDI" +
            "JBhMtMTgxIagDBjm/MFkXIyqXMVTDBCcGiAwArweRzpC92rYnoLqc2ue1WJ+t1qHvYjo/09ncnf769/cyqyn1KAAANYQKYCn8OgcRBYGjcwAfD" +
            "CNNMNIwwVMF4MR1CQDipwdExEkCA//tSxBOCDCgrBy5/hkGckaBFr9SSOuEU1WJDMAPAxuJhyGAtNNo0RCjqS9zCnQVvMVzup70/uoVfT0b2I3" +
            "pVt59O9Cc5LZ2tU0hVsr67u6AGhIiA4uZwIbsufZoYIWBDmIAAcxisQSGYLkoJmSHpUhgNYWqBh+ZAYcU4GDTEAQOwhDgCQOGCw00YRaU1fr33" +
            "r/6t6t63vt59dzar0bg8i45qdTn6FPb3VKfTvoXdLbEr6tnc3uIVgFSpD0qAodDooPhVGjt0NCI0wiQHlMUsFUz/+1LEDQILcC0ELn+GQUaFYX" +
            "GfbJDnBhLIxOADxPJnY2QQzOYgMiAkSJQcEEjF8vtixVzK56vb6UXSartVyKp2u3VoXZSp7KtiFbfTvr2k0Pe0rt+reQABkAkVJmToaAos1iD7" +
            "hMJIGYyUQhzMLEqMDW6Y5LK1TDGGWMJoyVSHCsqig6CpUMEc+TDthbaj9zar9en92q9quzdRuT/eyvRvlNncxC76vVcqR5GQAQA0lEA4RwanDE" +
            "mdOboYwxMJeMXvIqDrax80xXsFuPmK83SYjf/7UsQUAkukLwIOf4ZBUYWg4a/skBA3MohgFFkSDaM6qzs2taV9O+ralU4itzUq0IupLOpRyPYX" +
            "z5Ep0rsZXo3V7arjVd0v6r6VAFCB06AUDMcINOdOpEMDFAtTC9gMIw8gFbMCfOqTQJzaQwZ8H/NOzjC2sKHQhIQsHA0BTrX5C7zO5Oz2pVkF9G" +
            "76O1Cr++nfVe+rle3++r1307v+ylMkjRkCCgXCo0MFH0yDUjwBVMMoCNTFJiZE5LoiwMSoBgzxKyNjGMzmNjIQZAxGHgQo//tSxBkDyuwtBA5/" +
            "hkFDhWDBr+yQora71kivZVbTsSrQq6Q3N7iG4zVd367q1Pd3U3W+ndX0+jf7fY/Cg5Zgx400B46M0wLoDMMLXAoTDdwRAwZI4INPKNIzCEAfA1" +
            "+zMkWjDTcGDAFCwIAK4V/AgMLr3U7vbXdqtT9WhV/07vPJ+qc1/07PZV/7++phrAkJoqACAgAbMARAVDANgOMwUcCyMMVBpDD9y243CsY1MNtB" +
            "JDmCfNBkIykKTFoSDAyjE2Nc1LaUKVELQOO/2065aDf/+1LEIoIL2GkGD/BKgSCFYmG/ZMxpZWnf/21x/uRvoU9tXWvq6N0XWmjs7PYrpAzcc4" +
            "w5a7EA5gwUZWbn1G5hqAWmFqbibwaXhhLhEm1QAS0BCt7WHLjc52/KP0/V73d9HSvoXr+W6M9Vdrnrttf4onnyX1lalQAAABQ8Ykkpq7brvgpo" +
            "CAYMEAZCDxAEAnmA0GTAOGAAJDwEtDeODxhO/U77+/3+xH+hX+u//d//9jW1ob9F2hYjAHMCYG4hACCoLq3BGBYZyJHBpYQcmPoZqf/7UsQtAA" +
            "f8Ix210YAiL45ggz0wAK1alxglj7GQUSyYTwIhgLgHmHOAcLAoGL0HIYUgImm4Hgwlac1oHxAMPUIAh+xokRQ0u6rLdbl8zLx4mWeaJwQJOBQT" +
            "pk13nT9giSVl0pMgwA3y72mwkNuljjUCyKzoTKBjFm62oqLqYsrtd/9ZVn/1pQIJWel6cN/5gIgcGCMNLswuRpvMQISM2V1ZTEtDpO9hDoxUwz" +
            "j+1ZaMuIC8wCbJENh6BemJLWtckjpNRZ0Ekpky0UVPWg+klSXM//tSxCUAENyrBjnmgAEvBeLLvPAEmrrTtRnVtdlPnyaEFzpAQFkm0Jyp9Y97" +
            "QC0VPMTNtCBE4H0xPWkWDwBH0VFHMIKYowJByhOBP/5cd/+oPi1KX5Y5VeIgpIpEggAcwAgMzByBxMDoMUyjJUwh6IBByhUBkoA5AqAek4b7KD" +
            "IMgsCwSDcGQrR7u/d3rHeqr+i0Z6f0/0f+e/sXp3r//+ruLImEQgYiEJjwlmTUIaChpgnYcQZXA0kGLphXh/W+amsGACIQDGLAYGGgcAy98VX/" +
            "+1LEGYAIIC8IDn9qAPeFpLwu5Jzkf/R/V/////////9jrqkCAAACd/66W2y4ODGqxJxlwjwJmHAAHWJjAazBdsWITrcOAJugZ277iyVZDd/46/" +
            "XVTZ/q37+9T0d32r/X///iuAwcWoAXRm/AECVMBKBSDA9xGEyRCBtMTdDbTcEqM2pUycWDGQmMYgsHDQMCchdYg1J9nVhHR+pCSa///16K/3L/" +
            "XWy/iqf/ot///qmQAA5hMOGKhWZKK5mdHGso2YNWHXGdDNqBj5YWwdacpv/7UsQ4A8mQLwYM/4hBPAXgwc/xQH8rGHAKCQMYUB5g8CGAADF+LX" +
            "E3Kd9CE/7mJ/uSUkhf/7/+n33Mp1sS27j1jAAAAJ1TT/d/7d0lCEOKgAw8Csh+1pihBzHay6UbCwWhxMcmcBAY1D5g0LgAFF90yMurbV8o+3xn" +
            "/9j6v8Xu09Fnfp7v//9HwAAAFNibSc1/f+xI3tYMjcSIiLwwDRATFWf1MK0MMzMgKeWoU0Xu3kL5xu17+r0ez/1939u3/++50XXZ3fT7vdWq//" +
            "/1bgYC//tSxEgACPQvD417igEGBeO1n2VEmEjBkBGZ8hmvth1muYNCFpGckKjhj1YRUdYU5oMaERNJQYYGCJgUCFnZf17f/R/u/V9f//q+5nqq" +
            "/1WkkABRRxIkq23DxCA5I+bTC7ZiATH6+cb6DAPmhQut24Yp6mXXkoUlut4jI6avrT//7u6yvp7v//4yuAAAAR0qD/eZ/JmpI0g4xjoDAPR0JA" +
            "EkGmGp8qYNIy5hPxgWphiQOCDw9KBj97s0IT2eWu2VdXf2Zbq//3rXv6dX7Nf/R///+1LEYQAIFC8IDf+KAOiF5TQ+YJ7rqewMDTCSIyIrNMST" +
            "d2w9jPMILCsjRrFCoyYEIOPZJk1aJgMgAsHjAAdMBgwGAeX9Wir29P2bv6/H//f/aj9y6LUt/8VqsAAFxqV7/900tCYFQVSgIAzLig3uBMUoew" +
            "7m+qjY6GAONn8zwQjIIxMMh0wYDCy6GFN1OokrPdb6ELVanTetnZu9m99f//s7Nv/ulAAEpjjj5/9+QtsmqWuMGQADMwPwwFByTFn2fMLIWEwe" +
            "ghTgwQhQUA1CWv/7UsSBgAi0LxOMe0hhGoXgwb/xQJXv2NV7lOo/c//f91O/fq/1zCNL7aauqv//9MoLB5gZIY4UmkH5vKofJOmEUhCBpWh8sZ" +
            "QiDHAfONIGvQMBk4JAYCEwDhCASDgFgSTf9Pzn/3fzU1/r////Z1//7ur9Lf//yVvBgAYGDl6ASHmbKRyeqYMUF8GbdrIRjsQSoB1dmgaYNgGW" +
            "SaBisWgYeCwGCwKDYQnvvW/OPu/qf/7uv///TbQAAAII4m213/58VYMXxAQUYaMGGHpjMcYJ//tSxJkBCWAvDS37igEKBeK1r2lAg/5mf+WmNg" +
            "MWbXsCFogEI0DwZIFf17TyI7PWb5YnS79ejZ2KezZ7/9Vn/pv/ZMgIAAASva6SSujYKCW5OysVHIIBkxFB48KYw2ZA4XpKE1YHAguPTO+rT2e+" +
            "31K9X9l3q+L///760/UumgMfpYwAAACKtVfz9+5agCANQgqhZiB0aTOmBvBHBkEaGoYi0DeHhyJqSMZeVmKiZhwEEAaYFrq07/vo/316PhBOv9" +
            "Hu/6NGjf5Gj3r+nABIrNr/+1LEsAPJAEkGDf6qAQaJIQG/1UD9/xdJaIwkDTEocMbkwyAizNMeMD8DwzI32+0xLoLrPF8DNmclHigZBR0EEIYD" +
            "SDpyeeYjRo9lnV+pr5T6WygqZOir2UbfqZ2G7na3MqMb/kgW8AAINFqJvXP17ZE/R0IDAZkgpsQADsmJCBAdGwyRq8gFDfIAr8yYMAAYKgqfij" +
            "re2uRT0+/3U/7/qnLR+tv/2JJ/aT6XXf1dVf//2i0ZlhRsRYC2mM1mAfAfBgZ4Q6YRkOHGm057BlOwsf/7UsTJAAj0LxOt+0oA/IXkfC7knEB/" +
            "n5gb4hAGqVeBm0vgZiGAGPQmBhwFCxPUzJuu7Sy6CrPprWut1V0Gn+l2X2W7n3R4suwBroFZUkWQPUyZMGggVQ+Wck0CcTLUkfScihUoFWxdhJ" +
            "x0gNDOB5EXQvnVgAOP73va7SzRhwiZGPGdpBpUCcP/GCWBwplfrQaYwEFWnB2oZWKpgUHFrTCoCAQVBwCnPmaa5sn9vV06qnTumN7fo+r99X/d" +
            "9d2xOP1IABrX62ooSh5IOAAXMsCy//tSxOOACVAvD43/agFaBeEZz+1ALWPYKjCHQPM0vQa/MoZA4QPqEQDYo7AzMOgMPDQDBIjAWCAIQSTNaP" +
            "WhrrbmZv937fb6+a1bK/fZQ7t1cu7atLmo3CtLEabU22bcV+jQAACA7/76wcSAHozYBMzkQAQhgGoJSYGIH4GR2unBhkAZIBqOGgZQQgGPSWBi" +
            "sNgYoAQDArAsCCS+qpldHrP9rXV/b+ufZvs094tbcy3legyyirKtelf1IoZo6Hc6Ox7iEgAAADQjLjS5+/+GWVL/+1LE8QIJJC0RTXtqAf2TH8" +
            "Gv1QgmlljFjjLIza1DCUFNNSuiEzPg7T+vjRC0gmYpjLFa0c5Ct0nucJy+5aGXJ/10rfUot6RMxf/YvmV+P+sr/b30KsAhAoOf7w4sgVCREKDA" +
            "WGHJqomd0wGDugvZoXhaqZEiC3geSPAGrySBlofAYiFwGDw6AICgHgMmfZnX9tR0/qZBFl10lNu7Vepekr/Z9TKZlqXnFDdKkqDNwGaGrNyX+5" +
            "w9KzdfWhMAvv6hZDBh+KMJSAAATn63zrzp4P/7UsTqgQmsMQit/4oBcpFg1b/VQjQAkYAgAAmAHAFYNAZASBymAiBixilK4QYC8E4GV5ZjS4Y2" +
            "WmKg4cYEQulgGr+rdNI0feeZL6LzbWTKLVfTS1M0B0MrZYVvP3MboP7dRTq/pkX+5eqgZcAABz9a9SkKgxhA2Y+SGhoRsjOdzmGDdBZZm5KoWZ" +
            "GwDwmDAgSBgSgBOGAM4FagYaAYEgGkLlaq6SnTzjLqLJ/VVu193Wgutqtj1JW7V1tuvrWtVOdzIJqcYIJAk/QGiOPcqXVh//tSxPOADCyLBwz+" +
            "qEE3BaJ1r2lAJI9Y8u/+4ICdb0g9KX6Xh7MIAJHea7tuCR4gAAUXFgBDArAbMG4GIxMRcTt6r/NWEWE/xuNrOTNigxIWMHCS2CX4Dypa64K1Qb" +
            "9kha1bFy//e7M3NqSXyQvUZXY/31LVtVaR7lXhVOhnw1fpsaAADTh6sP5/ydvU1i3pgB4FYAjyCRvjIz1UMXEVoDGZwkmANEhaeDAAdoT6f7Wd" +
            "Tvq87uC4ou88gVFpb3RiNOcNC6nCIqloVEoQhCfCy6T/+1LE+YAOJMEEzf6qAYIRYOH9lYhZKs2ORRLvM5MmyAyY2q3RZQVHK3iAAECjLY/r+Q" +
            "CvIvqWeMaINATOayMMwPE2g3rzR8BlMLUA4eB9BwEYhABSeWBX0mpEIhtmvwvz+1UIvWWrKsVYxlK3Tr2klh4ZOQik5CGkJG0qZDvYo0ktcjqd" +
            "vaWCBskKUX3ABDRow1qVkAAAgMv/XxBraxG3UMMAUBkwLgUDCfEpN5yOMySRPT5WjWpzLEjCAggGpYy8Vm+XM800LbImz1X8k7aYa//7UsTugo" +
            "7gwwCt/mpRawpgoe2JiL3Qmrv2pml2iL2Iy6Y3Wp4qJgUcrWi5qEMc8XyQ2VY85VeJlKl6rXNXRjvn4y53VTF4TBCTEnDLTzBBE6M5maQz4Q2j" +
            "BqA5MBcApTVwlSsSYC7qea03s7NOhcsy2NdjOvVlVUbSNDm0vSq2NOVDambHDj6Oyta3ffvZThdXsy1ozJNRUulTS6EN6srVD5JSxWp2rlR5wj" +
            "RvQBqIhmddhOpLapmpYuCQBd5YyKoBDGSrnePmXUAkGiYpmBcS//tSxOOADHRTCS16igGmkWClrxVQD9Eu0RmFpMtY4FedUe0MMCo07gr8rxE/" +
            "5GgO7BEa9m2EgaeWXq8t1uWd/LBhADAbkBjwwUCgRMaS8DmV8rTmJ6TIdxMzQPMdNYmcLF1GzP/8BBIVDP//+ziwr/8VFBbFhXWKN/i7P1ijah" +
            "YVTEFNRTMuMTAwqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqr/" +
            "+1LE2oFM1MMBD2isQdcmH4GvHVGqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq" +
            "qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq" +
            "qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqv/7UsTKAAj0MQRDaYbBAYfZHGelUKqqqqqqqqqqqqqqqqqqqqqqqq" +
            "qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq" +
            "qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq" +
            "qqqqqq";

        // A reminder chime should sit UNDER the pet, so play at a fraction of full. PlaySound scales
        // this by the user's master volume on top, so a master of 0 is still silence.
        private const double ChimeVolume = 0.6;

        // A custom chime is read straight off disk and handed to the host, which accepts a self-describing WAV
        // or MP3 up to 16 MiB (AudioOutput.MaximumModuleAudioBytes) and rejects anything else. Read within a
        // safe cap here; a blank path, a missing/oversize file, or a read error simply falls back to the default.
        private const long MaximumCustomBytes = 8 * 1024 * 1024;

        /// <summary>
        /// Play the default chime once. Best-effort and silent on failure: decoding or the host call may throw
        /// or return false, and in every case the caller simply gets no sound.
        /// </summary>
        public static void Play(IHost host) { Play(host, null); }

        /// <summary>
        /// Play a reminder chime once. When <paramref name="customPath"/> points at a readable WAV/MP3 within the
        /// size cap, that file is played; otherwise the embedded default chime is used. Best-effort and silent
        /// on failure, so a reminder still announces even if audio is unavailable.
        /// </summary>
        public static void Play(IHost host, string customPath)
        {
            if (host == null) return;
            try
            {
                byte[] audio = LoadCustom(customPath) ?? Convert.FromBase64String(ChimeMp3Base64);
                // Same module id the host checks ModulePermissions.Audio against.
                host.PlaySound(ReminderModule.Id, audio, ChimeVolume);
            }
            catch
            {
                // A chime is decoration; a reminder must still announce even if audio is unavailable.
            }
        }

        private static byte[] LoadCustom(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var info = new System.IO.FileInfo(path.Trim());
                if (!info.Exists || info.Length == 0 || info.Length > MaximumCustomBytes) return null;
                return System.IO.File.ReadAllBytes(info.FullName);
            }
            catch { return null; }
        }
    }
}