using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;
using Microsoft.Win32.SafeHandles;

namespace DesktopAICompanion
{
    /// <summary>Secure parser plus semantic/resource validation for every pet XML entry path.</summary>
    internal static class CompanionXmlValidator
    {
        internal sealed class RetainedLocalXmlFile : IDisposable
        {
            private SafeFileHandle handle;
            private List<SafeFileHandle> directoryHandles;

            internal RetainedLocalXmlFile(
                string canonicalPath,
                SafeFileHandle retainedHandle,
                List<SafeFileHandle> retainedDirectoryHandles)
            {
                CanonicalPath = canonicalPath;
                handle = retainedHandle;
                directoryHandles = retainedDirectoryHandles;
            }

            internal string CanonicalPath { get; private set; }

            internal FileStream OpenRead(int bufferSize)
            {
                if (handle == null || handle.IsClosed || handle.IsInvalid)
                    throw new ObjectDisposedException("RetainedLocalXmlFile");
                SafeFileHandle ownedHandle = handle;
                handle = null;
                try
                {
                    return new FileStream(
                        ownedHandle,
                        FileAccess.Read,
                        bufferSize,
                        false);
                }
                catch
                {
                    ownedHandle.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                if (handle != null)
                {
                    handle.Dispose();
                    handle = null;
                }
                if (directoryHandles != null)
                {
                    for (int index = directoryHandles.Count - 1;
                         index >= 0;
                         index--)
                        directoryHandles[index].Dispose();
                    directoryHandles = null;
                }
            }
        }

        private const uint GenericRead = 0x80000000;
        private const uint FileReadAttributes = 0x00000080;
        private const uint OpenExisting = 3;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagSequentialScan = 0x08000000;
        private const uint DriveUnknown = 0;
        private const uint DriveNoRootDirectory = 1;
        private const uint DriveRemote = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetDriveType(string rootPathName);

        internal static Action<string> LocalXmlHandleOpenedForDiagnostics
        {
            get;
            set;
        }

        private enum RequiredImageContainer
        {
            Png,
            Icon
        }

        private struct IconPayloadRange
        {
            internal int Start;
            internal int End;
        }

        // 12 MiB (raised from 4): a frame-heavy skin needs a bigger stored sheet to reach the full 256px
        // per-frame runtime size instead of being squeezed smaller by the old 4 MiB budget. The runtime still
        // downsamples every classic frame to <=256px on load (ReadImages), so this raises stored file size, NOT
        // per-pet render memory; the 4096px sheet + 16 Mi pixel caps (the memory guard) and the frame cap are
        // unchanged. A tick-clock companion's frame cap is its own, 768px (MaximumFrameDimensionFor).
        public const int MaximumXmlBytes = 12 * 1024 * 1024;
        public const int MaximumImageBytes = 12 * 1024 * 1024;
        public const int MaximumIconBytes = 512 * 1024;
        public const int MaximumAudioBytesPerSound = 2 * 1024 * 1024;
        /// <summary>The per-clip limit of a <c>&lt;sound&gt;</c> that declares a <c>format</c> (grimoire/06 section 3). WAV is
        /// uncompressed, so a real skin's pose clip outgrows the MP3-sized 2 MiB (lane feature/shimeji-host-wav measured 27
        /// members refused by it, 18 of which fit 4 MiB). Only a DECLARED clip gets it, because the attribute needs feature
        /// 'wav' (<see cref="WavFormat.DeclaresFormat"/> is the one reading of "declares", for both): a host older than the
        /// token refuses the companion by that name before any size check, never with a size error that reads as a broken
        /// file. The document total (<see cref="MaximumAudioBytesTotal"/>) is unchanged, so a document's decoded audio is
        /// bounded exactly as before; see <see cref="MaximumAudioBytesFor"/>.</summary>
        public const int MaximumDeclaredAudioBytesPerSound = 4 * 1024 * 1024;
        public const int MaximumAudioBytesTotal = 8 * 1024 * 1024;
        public const int MaximumImageDimension = 4096;
        public const long MaximumImagePixels = 16L * 1024L * 1024L;
        public const int MaximumIconEntries = 256;
        public const int MaximumSpriteFrameDimension = 256;
        public const int MaximumSpriteTiles = SpriteFrameStore.MaximumFrames;
        public const int MaximumAnimations = 1024;
        public const int MaximumFramesTotal = 16384;
        public const int MaximumTransitions = 256;
        public const int MaximumSpawns = 256;
        public const int MaximumChildren = 256;
        public const int MaximumSounds = 256;

        /// <summary>The largest frame side a companion's sheet may have: <see cref="ShimejiFormatValidator.TickMaximumFrameDimension"/>
        /// for a tick-clock companion, <see cref="MaximumSpriteFrameDimension"/> for a classic one. The one reading, for the
        /// validator and the loader's staging (Xml.ReadImages) alike, so a frame the validator admits is never one the
        /// loader refuses. An older host has only the classic cap, so it refuses a tick companion with a larger frame here,
        /// before anything is staged: "sprite image contains a frame larger than the runtime supports".</summary>
        internal static int MaximumFrameDimensionFor(bool tick)
        {
            return tick ? ShimejiFormatValidator.TickMaximumFrameDimension : MaximumSpriteFrameDimension;
        }

        /// <summary>The decoded size one sound may have: <see cref="MaximumDeclaredAudioBytesPerSound"/> when it declares a
        /// format (which needs feature 'wav', checked before any sound is decoded), else <see cref="MaximumAudioBytesPerSound"/>,
        /// a classic sound's and an undeclared (MP3) named clip's alike. A clip declared format="mp3" counts as declared: the
        /// attribute, whatever its value, is what needs the token. Decoded memory, at the mixer's 44.1 kHz stereo float
        /// (AudioOutput, 352,800 bytes a second, decoded once per clip and held while its bytes are): a WAV clip costs at most
        /// 44.1 times its bytes (8-bit mono at the 8 kHz floor), so a 4 MiB clip at most about 176 MiB; an MP3 at 8 kbps
        /// costs about 353 times its bytes. A document's clips together stay within 8 MiB of audio, so its decoded worst
        /// case is the one it had before the raise; the raise moves only what ONE clip may cost.</summary>
        internal static int MaximumAudioBytesFor(XmlData.SoundNode sound)
        {
            return WavFormat.DeclaresFormat(sound) ? MaximumDeclaredAudioBytesPerSound : MaximumAudioBytesPerSound;
        }

        /// <summary>A byte count as the "N MiB" a user reads, exact when it is a whole number of MiB (every cap
        /// here is) and to one decimal otherwise, so a raised cap never leaves a refusal naming the old figure
        /// (N-burn-tools-03; the converter's residue has formatted this way since RA-376). The two oversize refusals
        /// below and FormCompanion's drop read format from it, and a source invariant holds this file free of a
        /// literal figure.</summary>
        internal static string MebibytesOf(long bytes)
        {
            const long Mi = 1024L * 1024L;
            if (bytes % Mi == 0) return (bytes / Mi).ToString(System.Globalization.CultureInfo.InvariantCulture) + " MiB";
            return ((double)bytes / Mi).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MiB";
        }

        public static bool TryResolveLocalXmlFile(
            string path,
            out string canonicalPath,
            out string error)
        {
            RetainedLocalXmlFile retained;
            if (!TryOpenLocalXmlFile(
                    path,
                    out retained,
                    out error))
            {
                canonicalPath = null;
                return false;
            }
            using (retained)
                canonicalPath = retained.CanonicalPath;
            return true;
        }

        internal static bool TryOpenLocalXmlFile(
            string path,
            out RetainedLocalXmlFile retained,
            out string error)
        {
            retained = null;
            error = null;
            List<SafeFileHandle> directoryHandles = null;
            SafeFileHandle fileHandle = null;
            try
            {
                string candidate = CanonicalizeLocalPath(path);
                if (!string.Equals(
                        Path.GetExtension(candidate),
                        ".xml",
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The local pet must be an XML file.");

                directoryHandles = OpenRetainedDirectoryChain(candidate);
                fileHandle = CreateFile(
                    candidate,
                    GenericRead,
                    FileShare.Read,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagOpenReparsePoint | FileFlagSequentialScan,
                    IntPtr.Zero);
                if (fileHandle.IsInvalid)
                    throw OpenFailure(
                        "Could not open the local pet XML.",
                        Marshal.GetLastWin32Error());

                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(fileHandle, out information))
                    throw OpenFailure(
                        "Could not validate the local pet XML handle.",
                        Marshal.GetLastWin32Error());
                FileAttributes attributes =
                    (FileAttributes)information.FileAttributes;
                if ((attributes & (FileAttributes.Directory |
                                   FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidDataException(
                        "The local pet must be an existing reparse-free file on a local drive.");

                long length =
                    ((long)information.FileSizeHigh << 32) |
                    information.FileSizeLow;
                if (length > MaximumXmlBytes)
                    throw new InvalidDataException(
                        "The local pet must be no larger than " + MebibytesOf(MaximumXmlBytes) + ".");

                retained = new RetainedLocalXmlFile(
                    candidate,
                    fileHandle,
                    directoryHandles);
                fileHandle = null;
                directoryHandles = null;
                Action<string> diagnostic =
                    LocalXmlHandleOpenedForDiagnostics;
                if (diagnostic != null) diagnostic(candidate);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (fileHandle != null) fileHandle.Dispose();
                if (directoryHandles != null)
                    for (int index = directoryHandles.Count - 1;
                         index >= 0;
                         index--)
                        directoryHandles[index].Dispose();
                if (retained != null && error != null)
                {
                    retained.Dispose();
                    retained = null;
                }
            }
        }

        private static string CanonicalizeLocalPath(string path)
        {
            string value = (path ?? "").Trim();
            if (!IsDriveQualifiedAbsolutePath(value))
                throw new InvalidDataException(
                    "The local pet must be an absolute path on a local drive.");
            string candidate = Path.GetFullPath(value);
            if (!IsDriveQualifiedAbsolutePath(candidate))
                throw new InvalidDataException(
                    "The local pet path is not supported.");
            uint driveType = GetDriveType(candidate.Substring(0, 3));
            if (driveType == DriveUnknown ||
                driveType == DriveNoRootDirectory ||
                driveType == DriveRemote)
                throw new InvalidDataException(
                    "The local pet must be on a local drive.");
            return candidate;
        }

        private static bool IsDriveQualifiedAbsolutePath(string value)
        {
            return value != null &&
                value.Length >= 3 &&
                ((value[0] >= 'A' && value[0] <= 'Z') ||
                 (value[0] >= 'a' && value[0] <= 'z')) &&
                value[1] == Path.VolumeSeparatorChar &&
                (value[2] == Path.DirectorySeparatorChar ||
                 value[2] == Path.AltDirectorySeparatorChar);
        }

        private static List<SafeFileHandle> OpenRetainedDirectoryChain(
            string filePath)
        {
            string root = Path.GetPathRoot(filePath);
            string relative = filePath.Substring(root.Length);
            string[] segments = relative.Split(
                new[]
                {
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                },
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
                return new List<SafeFileHandle>();

            var handles = new List<SafeFileHandle>(segments.Length - 1);
            string current = root;
            try
            {
                for (int index = 0; index < segments.Length - 1; index++)
                {
                    current = Path.Combine(current, segments[index]);
                    SafeFileHandle handle = CreateFile(
                        current,
                        FileReadAttributes,
                        FileShare.Read | FileShare.Write,
                        IntPtr.Zero,
                        OpenExisting,
                        FileFlagOpenReparsePoint |
                            FileFlagBackupSemantics,
                        IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        int error = Marshal.GetLastWin32Error();
                        handle.Dispose();
                        throw OpenFailure(
                            "Could not retain the local pet directory chain.",
                            error);
                    }

                    ByHandleFileInformation information;
                    if (!GetFileInformationByHandle(handle, out information))
                    {
                        int error = Marshal.GetLastWin32Error();
                        handle.Dispose();
                        throw OpenFailure(
                            "Could not validate the local pet directory chain.",
                            error);
                    }
                    FileAttributes attributes =
                        (FileAttributes)information.FileAttributes;
                    if ((attributes & FileAttributes.Directory) == 0 ||
                        (attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        handle.Dispose();
                        throw new InvalidDataException(
                            "The local pet directory chain contains a reparse point.");
                    }
                    handles.Add(handle);
                }
                return handles;
            }
            catch
            {
                for (int index = handles.Count - 1; index >= 0; index--)
                    handles[index].Dispose();
                throw;
            }
        }

        private static IOException OpenFailure(string message, int error)
        {
            return new IOException(
                message + " " + new Win32Exception(error).Message,
                new Win32Exception(error));
        }

        public static bool TryParse(string xml, out XmlData.RootNode root, out string error)
        {
            byte[] spriteBytes;
            byte[] iconBytes;
            byte[][] soundBytes;
            return TryParse(xml, out root, out spriteBytes, out iconBytes, out soundBytes, out error, CancellationToken.None);
        }

        /// <summary>
        /// As <see cref="TryParse(string, out XmlData.RootNode, out string)"/>, and hands back the sprite sheet
        /// and icon bytes the validation already decoded and proved (F318). The runtime loader decoded both
        /// again from the same base64 -- a second pass over a multi-megabyte string, on the UI thread, per
        /// staged pet -- so the bytes this pass produced are what <c>Xml.TryReadXml</c> reads now. ADDITIVE:
        /// PetStudio source-links this file and calls the three-argument form, which is unchanged.
        /// </summary>
        public static bool TryParse(
            string xml,
            out XmlData.RootNode root,
            out byte[] spriteBytes,
            out byte[] iconBytes,
            out string error)
        {
            byte[][] soundBytes;
            return TryParse(xml, out root, out spriteBytes, out iconBytes, out soundBytes, out error, CancellationToken.None);
        }

        /// <summary>
        /// As the five-argument form, and hands back every <c>&lt;sound&gt;</c>'s decoded bytes as well, in
        /// document order (RA-271). The validation decodes each sound to prove it is an MP3 and used to discard
        /// the bytes, so the loader decoded the identical base64 a second time per staged type, on the UI
        /// thread, and the strings stayed in the graph beside the same text in the canonical document. ADDITIVE,
        /// like its sibling: PetStudio and ShimejiConvert.Engine source-link this file and call the shorter
        /// forms, which are unchanged.
        /// </summary>
        public static bool TryParse(
            string xml,
            out XmlData.RootNode root,
            out byte[] spriteBytes,
            out byte[] iconBytes,
            out byte[][] soundBytes,
            out string error)
        {
            return TryParse(xml, out root, out spriteBytes, out iconBytes, out soundBytes, out error, CancellationToken.None);
        }

        /// <summary>
        /// As the three-argument form, against an explicit set of supported Shimeji runtime features instead of this
        /// build's <see cref="CompanionFormatFeatures.Supported"/>. For self-tests and the converter's checks, which
        /// must validate an extension document before (or regardless of whether) the host runtime runs it. Nothing
        /// that LOADS a companion calls this. <paramref name="runsClass"/> likewise stands in for this build's answer to
        /// which embedded classes run (null keeps <see cref="CompanionFormatFeatures.RunsClass"/>), so a self-test can
        /// prove the per-class refusal after every family has landed.
        /// </summary>
        internal static bool TryParseWithFeatures(
            string xml,
            IReadOnlyCollection<string> supportedFeatures,
            out XmlData.RootNode root,
            out string error,
            Func<string, bool> runsClass = null)
        {
            byte[] spriteBytes;
            byte[] iconBytes;
            byte[][] soundBytes;
            return TryParse(xml, out root, out spriteBytes, out iconBytes, out soundBytes, out error, CancellationToken.None,
                supportedFeatures ?? CompanionFormatFeatures.Supported, runsClass ?? CompanionFormatFeatures.RunsClass);
        }

        private static bool TryParse(
            string xml,
            out XmlData.RootNode root,
            out byte[] spriteBytes,
            out byte[] iconBytes,
            out byte[][] soundBytes,
            out string error,
            CancellationToken cancellationToken)
        {
            return TryParse(xml, out root, out spriteBytes, out iconBytes, out soundBytes, out error, cancellationToken,
                CompanionFormatFeatures.Supported, CompanionFormatFeatures.RunsClass);
        }

        // No public token-taking TryParse. Every caller in the tree (the loader, the catalog, the pane, the
        // converter, the self-tests) reaches the private parse below through the overloads above, which pass
        // CancellationToken.None, and the one caller holding a live token parses synchronously on the UI thread,
        // where nothing could cancel it (F253). The token still threads through the private validation as the
        // seam for a parse that runs off the UI thread one day; today it is None from every entry point.
        private static bool TryParse(
            string xml,
            out XmlData.RootNode root,
            out byte[] spriteBytes,
            out byte[] iconBytes,
            out byte[][] soundBytes,
            out string error,
            CancellationToken cancellationToken,
            IReadOnlyCollection<string> supportedFeatures,
            Func<string, bool> runsClass)
        {
            root = null;
            spriteBytes = null;
            iconBytes = null;
            soundBytes = null;
            error = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A leading UTF-8 BOM (U+FEFF) survives decoding into this string and makes
                // XmlSerializer throw "There is an error in XML document (1, 1)"; strip it so
                // BOM-prefixed pet files (e.g. the Mimiko pack) still load. Affects any pet/user file.
                if (!string.IsNullOrEmpty(xml) && xml[0] == '\uFEFF')
                    xml = xml.Substring(1);
                if (string.IsNullOrWhiteSpace(xml))
                    throw new InvalidDataException("Pet XML is empty.");
                if (xml.Length > MaximumXmlBytes ||
                    Encoding.UTF8.GetByteCount(xml) > MaximumXmlBytes)
                    throw new InvalidDataException("Pet XML exceeds the " + MebibytesOf(MaximumXmlBytes) + " limit.");

                // BEFORE the schema: a companion written for a newer host is told which feature it needs, instead of
                // failing on an element this schema has never heard of (grimoire/06 section 1).
                ShimejiFormatValidator.PreCheckFeatures(xml, supportedFeatures);

                XmlSchemaSet schemas = LoadSchema();
                string schemaError = null;
                XmlReaderSettings settings = CreateReaderSettings();
                settings.ValidationType = ValidationType.Schema;
                settings.Schemas = schemas;
                settings.ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings;
                settings.ValidationEventHandler += delegate(object sender, ValidationEventArgs args)
                {
                    if (schemaError == null) schemaError = args.Message;
                };

                var serializer = new XmlSerializer(typeof(XmlData.RootNode));
                using (var text = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(text, settings))
                    root = (XmlData.RootNode)serializer.Deserialize(reader);
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.IsNullOrEmpty(schemaError))
                    throw new InvalidDataException("XSD validation failed: " + schemaError);

                Validate(root, cancellationToken, out spriteBytes, out iconBytes, out soundBytes, supportedFeatures, runsClass);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                root = null;
                spriteBytes = null;
                iconBytes = null;
                soundBytes = null;
                error = ex.Message;
                return false;
            }
        }

        private static XmlReaderSettings CreateReaderSettings()
        {
            return new XmlReaderSettings
            {
                CheckCharacters = true,
                CloseInput = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = MaximumXmlBytes
            };
        }

        private static XmlSchemaSet LoadSchema()
        {
            var schemas = new XmlSchemaSet { XmlResolver = null };
            using (var text = new StringReader(Properties.Resources.animations1))
            using (XmlReader reader = XmlReader.Create(text, CreateReaderSettings()))
                schemas.Add("https://esheep.petrucci.ch/", reader);
            schemas.Compile();
            return schemas;
        }

        private static void Validate(
            XmlData.RootNode root,
            CancellationToken cancellationToken,
            out byte[] spriteBytesOut,
            out byte[] iconBytesOut,
            out byte[][] soundBytesOut,
            IReadOnlyCollection<string> supportedFeatures,
            Func<string, bool> runsClass)
        {
            spriteBytesOut = null;
            iconBytesOut = null;
            soundBytesOut = null;
            cancellationToken.ThrowIfCancellationRequested();
            if (root == null || root.Header == null || root.Image == null ||
                root.Animations == null || root.Animations.Animation == null ||
                root.Spawns == null || root.Spawns.Spawn == null ||
                root.Childs == null)
                throw new InvalidDataException("Pet XML is missing a required section.");

            RequireText(root.Header.Petname, 1, 128, "header/petname");
            RequireText(root.Header.Title, 1, 1024, "header/title");
            RequireText(root.Header.Author, 1, 1024, "header/author");
            RequireText(root.Header.Info, 0, 65536, "header/info");

            // The Shimeji runtime extensions (grimoire/06): every extension the document USES must be declared (the
            // pre-check before the schema already refused any declared feature this build does not run), and a
            // tick-clock companion gets the tick-mode limits and rules.
            HashSet<string> declared = ShimejiFormatValidator.CheckDeclaredUsage(root);
            bool tick = ShimejiFormatValidator.IsTickMode(root);
            int maximumAnimations = tick ? ShimejiFormatValidator.TickMaximumAnimations : MaximumAnimations;
            int maximumTransitions = tick ? ShimejiFormatValidator.TickMaximumTransitions : MaximumTransitions;
            int maximumFrameDimension = MaximumFrameDimensionFor(tick);

            if (root.Image.TilesX < 1 || root.Image.TilesX > 256 ||
                root.Image.TilesY < 1 || root.Image.TilesY > 256)
                throw new InvalidDataException("Sprite tile counts must be between 1 and 256.");

            int tileCount;
            try { tileCount = checked(root.Image.TilesX * root.Image.TilesY); }
            catch (OverflowException) { throw new InvalidDataException("Sprite tile count is too large."); }
            if (tileCount > MaximumSpriteTiles)
                throw new InvalidDataException("Sprite sheet contains too many tiles.");

            byte[] imageBytes = DecodeBase64(
                root.Image.Png,
                MaximumImageBytes,
                "sprite image");
            cancellationToken.ThrowIfCancellationRequested();
            byte[] iconBytes = DecodeBase64(
                root.Header.Icon,
                MaximumIconBytes,
                "pet icon");
            ValidateImage(
                imageBytes,
                root.Image.TilesX,
                root.Image.TilesY,
                "sprite image",
                RequiredImageContainer.Png,
                maximumFrameDimension,
                cancellationToken);
            // The icon keeps the classic cap whatever the clock: an ICO entry is at most 256 px anyway.
            ValidateImage(
                iconBytes,
                1,
                1,
                "pet icon",
                RequiredImageContainer.Icon,
                MaximumSpriteFrameDimension,
                cancellationToken);
            // Proven, and handed to the caller so the loader need not decode them again (F318).
            spriteBytesOut = imageBytes;
            iconBytesOut = iconBytes;

            XmlData.AnimationNode[] animations = root.Animations.Animation;
            if (animations.Length < 1 || animations.Length > maximumAnimations)
                throw new InvalidDataException("Pet must define 1 to " + maximumAnimations + " animations.");

            var ids = new HashSet<int>();
            foreach (XmlData.AnimationNode animation in animations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (animation == null || animation.Id < 1 || !ids.Add(animation.Id))
                    throw new InvalidDataException("Animation ids must be unique positive integers.");
                RequireText(animation.Name, 0, 128, "animation name");
            }

            int framesTotal = 0;
            foreach (XmlData.AnimationNode animation in animations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A tick-clock node plays poses inside its <act> and has no classic frames; the tick-mode rules
                // (ShimejiFormatValidator.ValidateTickDocument, below) refuse classic frames there.
                if (animation.Start == null || animation.End == null || animation.Sequence == null ||
                    (!tick && (animation.Sequence.Frame == null || animation.Sequence.Frame.Length == 0)))
                    throw new InvalidDataException("Animation " + animation.Id + " is incomplete.");

                ValidateMoving(animation.Start, "animation " + animation.Id + " start");
                ValidateMoving(animation.End, "animation " + animation.Id + " end");
                ValidateExpression(animation.Sequence.RepeatCount, "animation " + animation.Id + " repeat");

                if (!tick)
                {
                    framesTotal = checked(framesTotal + animation.Sequence.Frame.Length);
                    if (framesTotal > MaximumFramesTotal)
                        throw new InvalidDataException("Pet defines too many animation frames.");
                    foreach (int frame in animation.Sequence.Frame)
                        if (frame < 0 || frame >= tileCount)
                            throw new InvalidDataException("Animation " + animation.Id + " references an invalid frame.");

                    if (animation.Sequence.RepeatFromFrame < 0 ||
                        animation.Sequence.RepeatFromFrame >= animation.Sequence.Frame.Length)
                        throw new InvalidDataException("Animation " + animation.Id + " has an invalid repeat-from frame.");
                }

                if (!string.IsNullOrWhiteSpace(animation.Sequence.Action) &&
                    !string.Equals(animation.Sequence.Action, "none", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(animation.Sequence.Action, "flip", StringComparison.OrdinalIgnoreCase) &&
                    // faceCursor: aim at the pointer when the animation STARTS. Added for converted gaze
                    // poses ("sit and look at the mouse"), which are meaningless without a direction.
                    // Unlike flip, which toggles at the sequence end, this sets facing absolutely on entry.
                    !string.Equals(animation.Sequence.Action, "faceCursor", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Animation " + animation.Id + " has an unsupported action.");

                ValidateNextSet(
                    animation.Sequence.Next,
                    ids,
                    "animation " + animation.Id + " sequence",
                    cancellationToken,
                    maximumTransitions,
                    tick);
                if (animation.Border != null)
                    ValidateNextSet(
                        animation.Border.Next,
                        ids,
                        "animation " + animation.Id + " border",
                        cancellationToken,
                        maximumTransitions,
                        tick);
                if (animation.Gravity != null)
                    ValidateNextSet(
                        animation.Gravity.Next,
                        ids,
                        "animation " + animation.Id + " gravity",
                        cancellationToken,
                        maximumTransitions,
                        tick);
            }
            if (tick)
            {
                // The cell a pose's image geometry must fit (grimoire/06 section 3); ValidateImage proved the sheet is a
                // PNG whose size divides into the tiles.
                int sheetWidth, sheetHeight;
                TryReadPngDimensions(imageBytes, out sheetWidth, out sheetHeight);
                ShimejiFormatValidator.ValidateTickDocument(root, tileCount, sheetWidth / root.Image.TilesX,
                    sheetHeight / root.Image.TilesY, declared, runsClass);
            }

            int spawnWeight = 0;
            var spawnIds = new HashSet<int>();
            if (root.Spawns.Spawn.Length > MaximumSpawns)
                throw new InvalidDataException("Pet defines too many spawn entries.");
            foreach (XmlData.SpawnNode spawn in root.Spawns.Spawn)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (spawn == null || spawn.Id < 0 || !spawnIds.Add(spawn.Id) ||
                    spawn.Next == null || spawn.Probability < 0 ||
                    spawn.Probability > 1000000)
                    throw new InvalidDataException("A spawn entry is invalid.");
                ValidateExpression(spawn.X, "spawn X");
                ValidateExpression(spawn.Y, "spawn Y");
                if (!ids.Contains(spawn.Next.Value))
                    throw new InvalidDataException("A spawn references a missing animation.");
                spawnWeight = checked(spawnWeight + spawn.Probability);
            }
            if (root.Spawns.Spawn.Length == 0 || spawnWeight <= 0)
                throw new InvalidDataException("Pet must have at least one positive-probability spawn.");

            XmlData.ChildNode[] children = root.Childs.Child ??
                new XmlData.ChildNode[0];
            if (children.Length > MaximumChildren)
                throw new InvalidDataException("Pet defines too many child animations.");
            foreach (XmlData.ChildNode child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child == null || !ids.Contains(child.Id) || !ids.Contains(child.Next))
                    throw new InvalidDataException("A child references a missing animation.");
                ValidateExpression(child.X, "child X");
                ValidateExpression(child.Y, "child Y");
            }

            XmlData.SoundNode[] sounds = root.Sounds == null || root.Sounds.Sound == null
                ? new XmlData.SoundNode[0]
                : root.Sounds.Sound;
            if (sounds.Length > MaximumSounds)
                throw new InvalidDataException("Pet defines too many sounds.");

            int audioTotal = 0;
            var soundProbabilityByAnimation = new Dictionary<int, int>();
            // Proven, and handed to the caller in document order so the loader need not decode them again
            // (RA-271), as the sheet and icon bytes are above.
            var soundBytes = new byte[sounds.Length][];
            int soundIndex = 0;
            foreach (XmlData.SoundNode sound in sounds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A NAMED clip (id=) belongs to a tick-clock companion, whose poses play it by name; it has no classic
                // animation and no per-animation probability, and ShimejiFormatValidator checks its name. A classic sound
                // is tied to an animation id. The audio checks below apply to both.
                bool named = sound != null && sound.SoundId != null;
                if (sound == null || (!named && !ids.Contains(sound.Id)) || (named && sound.Id != 0) ||
                    sound.Probability < 0 || sound.Probability > 100 ||
                    sound.Loop < 0 || sound.Loop > 20)
                    throw new InvalidDataException("A sound entry is invalid.");
                // A named clip plays when a pose names it, once per pose (grimoire/06): a probability or a loop on one
                // would be accepted and silently ignored, so they stay with classic sounds.
                if (named && (sound.Probability != 0 || sound.Loop != 0))
                    throw new InvalidDataException("Sound '" + sound.SoundId + "' is a named clip, which plays when a pose names it: "
                        + "<probability> and <loop> belong to classic sounds.");
                if (!named)
                {
                    int cumulativeProbability;
                    soundProbabilityByAnimation.TryGetValue(
                        sound.Id,
                        out cumulativeProbability);
                    cumulativeProbability = checked(
                        cumulativeProbability + sound.Probability);
                    if (cumulativeProbability > 100)
                        throw new InvalidDataException(
                            "Sound probabilities for animation " + sound.Id +
                            " exceed 100 percent.");
                    soundProbabilityByAnimation[sound.Id] = cumulativeProbability;
                }
                // The declared-format limit is safe to apply here because CheckDeclaredUsage (above, before any image or
                // sound is decoded) already required feature 'wav' of every sound that declares a format, and the pre-check
                // before the schema refused that feature by name on a host that does not run it.
                byte[] audioBytes = DecodeBase64(
                    sound.Base64,
                    MaximumAudioBytesFor(sound),
                    "sound");
                audioTotal = checked(audioTotal + audioBytes.Length);
                if (audioTotal > MaximumAudioBytesTotal)
                    throw new InvalidDataException("Pet audio exceeds the total size limit.");
                string audioError = AudioRefusal(sound, audioBytes);
                if (audioError != null)
                    throw new InvalidDataException(audioError);
                soundBytes[soundIndex++] = audioBytes;
            }
            soundBytesOut = soundBytes;
        }

        /// <summary>
        /// Null when a sound's bytes are the format it DECLARES, otherwise the refusal, naming the sound (grimoire/06
        /// section 3). The declaration decides which check runs; the bytes are looked at again only to word a mismatch, so
        /// the author is told which way round it is. A classic sound is always MP3: the format attribute needs feature
        /// 'wav', which needs 'tick', and a tick-clock companion's sounds are all named clips, so it never reaches here
        /// with one.
        /// </summary>
        private static string AudioRefusal(XmlData.SoundNode sound, byte[] audioBytes)
        {
            string error;
            if (WavFormat.IsDeclaredWav(sound))
            {
                if (WavFormat.IsDecodableWave(audioBytes, out error)) return null;
                string mp3Error;
                if (Mp3Format.LooksLikeMp3(audioBytes, out mp3Error))
                    return "Sound '" + sound.SoundId + "' is declared format=\"wav\" but holds MP3 audio; an MP3 clip carries no format.";
                return "Sound '" + sound.SoundId + "' is declared format=\"wav\" but " + error + ".";
            }
            if (Mp3Format.LooksLikeMp3(audioBytes, out error)) return null;
            if (WavFormat.HasWaveHeader(audioBytes))
                return sound.SoundId != null
                    ? "Sound '" + sound.SoundId + "' holds WAV audio but is declared MP3 (no format, or format=\"mp3\"): declare format=\"wav\" and feature 'wav'."
                    : "Sound for animation " + sound.Id + " holds WAV audio; a classic sound is MP3 (only a tick-clock companion's named clips may be WAV).";
            return sound.SoundId != null ? "Sound '" + sound.SoundId + "': " + error : error;
        }

        private static void ValidateMoving(XmlData.MovingNode moving, string location)
        {
            ValidateExpression(moving.X, location + " X");
            ValidateExpression(moving.Y, location + " Y");
            ValidateExpression(moving.Interval, location + " interval");
            if (moving.OffsetY < -32768 || moving.OffsetY > 32768 ||
                double.IsNaN(moving.Opacity) || double.IsInfinity(moving.Opacity) ||
                moving.Opacity < 0.0 || moving.Opacity > 1.0)
                throw new InvalidDataException(location + " has an invalid offset or opacity.");
        }

        private static void ValidateNextSet(
            XmlData.NextNode[] nextNodes,
            HashSet<int> ids,
            string location,
            CancellationToken cancellationToken,
            int maximumTransitions,
            bool tickMode)
        {
            if (nextNodes == null || nextNodes.Length == 0) return;
            if (nextNodes.Length > maximumTransitions)
                throw new InvalidDataException(location + " contains too many transitions.");
            int total = 0;
            foreach (XmlData.NextNode next in nextNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (next == null || next.Probability < 0 ||
                    next.Probability > 1000000 || !ids.Contains(next.Value))
                    throw new InvalidDataException(location + " contains an invalid transition.");
                if (!IsAllowedOnly(next.OnlyFlag))
                    throw new InvalidDataException(location + " contains an unsupported transition condition.");
                total = checked(total + next.Probability);
            }
            // A tick-clock list may weigh 0 in total: the source then drops the companion in at a random x and enters
            // Fall (Configuration.buildNextBehavior), so a pack whose replacing NextBehaviorList holds only
            // transition-only entries is legal there. A classic pet has no such fallback.
            if (total <= 0 && !tickMode)
                throw new InvalidDataException(location + " has no positive-probability transition.");
        }

        /// <summary>Internal rather than private so the accepted vocabulary can be asserted directly. A pet
        /// using an <c>only=</c> value the validator rejects is refused whole, so this list silently going
        /// stale is the difference between a converted pet loading and not loading at all.</summary>
        internal static bool IsAllowedOnly(string value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                   string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "taskbar", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "window", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "horizontal", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "horizontal+", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "vertical", StringComparison.OrdinalIgnoreCase) ||
                   // Which EDGE of a window. `window` stays as the wildcard that matches any of them, so
                   // every pet written before these existed is unaffected.
                   string.Equals(value, "window-left", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "window-right", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "window-top", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "window-bottom", StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateExpression(string value, string location)
        {
            string error;
            if (!SafeExpression.IsValid(value, out error))
                throw new InvalidDataException(location + " has an invalid expression: " + error);
        }

        private static byte[] DecodeBase64(string value, int maximumBytes, string location)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException(location + " is empty.");
            int marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            string encoded = marker >= 0 ? value.Substring(marker + 8) : value;
            if (encoded.Length > ((maximumBytes + 2L) / 3L * 4L) + 4096L)
                throw new InvalidDataException(location + " exceeds its encoded size limit.");
            byte[] decoded;
            try { decoded = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw new InvalidDataException(location + " is not valid base64."); }
            if (decoded.Length > maximumBytes)
                throw new InvalidDataException(location + " exceeds its decoded size limit.");
            return decoded;
        }

        private static void ValidateImage(
            byte[] bytes,
            int tilesX,
            int tilesY,
            string location,
            RequiredImageContainer requiredContainer,
            int maximumFrameDimension,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                int headerWidth;
                int headerHeight;
                bool png = TryReadPngDimensions(bytes, out headerWidth, out headerHeight);
                HashSet<long> iconDimensions = null;
                if (requiredContainer == RequiredImageContainer.Png && !png)
                    throw new InvalidDataException(location + " must be a PNG image.");
                if (requiredContainer == RequiredImageContainer.Icon)
                {
                    if (png)
                        throw new InvalidDataException(location + " must be an ICO image.");
                    iconDimensions = ValidateIconDirectory(
                        bytes,
                        tilesX,
                        tilesY,
                        location,
                        cancellationToken,
                        out headerWidth,
                        out headerHeight);
                }
                ValidateDimensions(
                    headerWidth,
                    headerHeight,
                    tilesX,
                    tilesY,
                    location,
                    maximumFrameDimension);

                using (var stream = new MemoryStream(bytes, false))
                using (Image image = Image.FromStream(stream, true, true))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateDimensions(
                        image.Width,
                        image.Height,
                        tilesX,
                        tilesY,
                        location,
                        maximumFrameDimension);
                    if (png && (image.Width != headerWidth || image.Height != headerHeight))
                        throw new InvalidDataException(location + " dimensions do not match its header.");
                    if (iconDimensions != null &&
                        !iconDimensions.Contains(
                            DimensionKey(image.Width, image.Height)))
                        throw new InvalidDataException(
                            location + " decoded an ICO frame not declared by its directory.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidDataException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidDataException(location + " could not be decoded: " + ex.Message);
            }
        }

        private static void ValidateDimensions(
            int width,
            int height,
            int tilesX,
            int tilesY,
            string location,
            int maximumFrameDimension)
        {
            if (width < 1 || height < 1 ||
                width > MaximumImageDimension || height > MaximumImageDimension ||
                (long)width * height > MaximumImagePixels ||
                width % tilesX != 0 || height % tilesY != 0)
                throw new InvalidDataException(
                    location + " has invalid dimensions or tile geometry.");
            // The cap the caller read for this document's clock (MaximumFrameDimensionFor). An older host refuses a tick
            // frame past its classic cap with this sentence, less the figure in brackets.
            if ((width / tilesX) > maximumFrameDimension ||
                (height / tilesY) > maximumFrameDimension)
                throw new InvalidDataException(
                    location + " contains a frame larger than the runtime supports (" + maximumFrameDimension + " px a side).");
        }

        private static bool TryReadPngDimensions(
            byte[] bytes,
            out int width,
            out int height)
        {
            return TryReadPngDimensions(
                bytes,
                0,
                bytes == null ? 0 : bytes.Length,
                out width,
                out height);
        }

        private static bool TryReadPngDimensions(
            byte[] bytes,
            int offset,
            int length,
            out int width,
            out int height)
        {
            width = 0;
            height = 0;
            if (bytes == null || offset < 0 || length < 24 ||
                offset > bytes.Length - length ||
                bytes[offset] != 0x89 || bytes[offset + 1] != 0x50 ||
                bytes[offset + 2] != 0x4e || bytes[offset + 3] != 0x47 ||
                bytes[offset + 4] != 0x0d || bytes[offset + 5] != 0x0a ||
                bytes[offset + 6] != 0x1a || bytes[offset + 7] != 0x0a ||
                bytes[offset + 12] != 0x49 || bytes[offset + 13] != 0x48 ||
                bytes[offset + 14] != 0x44 || bytes[offset + 15] != 0x52)
                return false;
            width = ReadBigEndianInt32(bytes, offset + 16);
            height = ReadBigEndianInt32(bytes, offset + 20);
            return width > 0 && height > 0;
        }

        private static HashSet<long> ValidateIconDirectory(
            byte[] bytes,
            int tilesX,
            int tilesY,
            string location,
            CancellationToken cancellationToken,
            out int width,
            out int height)
        {
            width = 0;
            height = 0;
            if (bytes == null || bytes.Length < 22 ||
                bytes[0] != 0 || bytes[1] != 0 ||
                bytes[2] != 1 || bytes[3] != 0)
                throw new InvalidDataException(location + " must be an ICO image.");

            int count = ReadLittleEndianUInt16(bytes, 4);
            if (count < 1 || count > MaximumIconEntries)
                throw new InvalidDataException(
                    location + " has an excessive ICO directory entry count.");
            int directoryBytes = checked(6 + count * 16);
            if (directoryBytes > bytes.Length)
                throw new InvalidDataException(
                    location + " has a truncated ICO directory.");

            var dimensions = new HashSet<long>();
            var ranges = new List<IconPayloadRange>(count);
            long aggregatePixels = 0;
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int entryOffset = 6 + index * 16;
                int declaredWidth =
                    bytes[entryOffset] == 0 ? 256 : bytes[entryOffset];
                int declaredHeight =
                    bytes[entryOffset + 1] == 0
                        ? 256
                        : bytes[entryOffset + 1];
                uint payloadLengthValue =
                    ReadLittleEndianUInt32(bytes, entryOffset + 8);
                uint payloadOffsetValue =
                    ReadLittleEndianUInt32(bytes, entryOffset + 12);
                if (payloadLengthValue == 0 ||
                    payloadLengthValue > int.MaxValue ||
                    payloadOffsetValue > int.MaxValue)
                    throw new InvalidDataException(
                        location + " has an invalid ICO payload range.");

                int payloadLength = (int)payloadLengthValue;
                int payloadOffset = (int)payloadOffsetValue;
                long payloadEndValue =
                    (long)payloadOffset + payloadLength;
                if (payloadOffset < directoryBytes ||
                    payloadEndValue > bytes.Length)
                    throw new InvalidDataException(
                        location + " has an out-of-range ICO payload.");
                int payloadEnd = (int)payloadEndValue;
                for (int previous = 0; previous < ranges.Count; previous++)
                    if (payloadOffset < ranges[previous].End &&
                        payloadEnd > ranges[previous].Start)
                        throw new InvalidDataException(
                            location + " has overlapping ICO payloads.");
                ranges.Add(new IconPayloadRange
                {
                    Start = payloadOffset,
                    End = payloadEnd
                });

                int embeddedWidth;
                int embeddedHeight;
                if (!TryReadPngDimensions(
                        bytes,
                        payloadOffset,
                        payloadLength,
                        out embeddedWidth,
                        out embeddedHeight) &&
                    !TryReadIconDibDimensions(
                        bytes,
                        payloadOffset,
                        payloadLength,
                        out embeddedWidth,
                        out embeddedHeight))
                    throw new InvalidDataException(
                        location + " has an unsupported ICO image payload.");
                if (embeddedWidth != declaredWidth ||
                    embeddedHeight != declaredHeight)
                    throw new InvalidDataException(
                        location + " has ICO directory/payload dimension mismatch.");

                ValidateDimensions(
                    embeddedWidth,
                    embeddedHeight,
                    tilesX,
                    tilesY,
                    location,
                    MaximumSpriteFrameDimension);
                aggregatePixels = checked(
                    aggregatePixels +
                    (long)embeddedWidth * embeddedHeight);
                if (aggregatePixels > MaximumImagePixels)
                    throw new InvalidDataException(
                        location + " has excessive aggregate ICO pixels.");
                dimensions.Add(
                    DimensionKey(embeddedWidth, embeddedHeight));
                if (index == 0)
                {
                    width = embeddedWidth;
                    height = embeddedHeight;
                }
            }
            return dimensions;
        }

        private static bool TryReadIconDibDimensions(
            byte[] bytes,
            int offset,
            int length,
            out int width,
            out int height)
        {
            width = 0;
            height = 0;
            if (bytes == null || offset < 0 || length < 12 ||
                offset > bytes.Length - length)
                return false;

            uint headerSize = ReadLittleEndianUInt32(bytes, offset);
            int rawHeight;
            if (headerSize == 12)
            {
                width = ReadLittleEndianUInt16(bytes, offset + 4);
                rawHeight = ReadLittleEndianUInt16(bytes, offset + 6);
                if (ReadLittleEndianUInt16(bytes, offset + 8) != 1)
                    return false;
            }
            else
            {
                if (headerSize < 40 ||
                    headerSize > length ||
                    headerSize > int.MaxValue)
                    return false;
                width = ReadLittleEndianInt32(bytes, offset + 4);
                rawHeight = ReadLittleEndianInt32(bytes, offset + 8);
                if (ReadLittleEndianUInt16(bytes, offset + 12) != 1)
                    return false;
                int bitsPerPixel =
                    ReadLittleEndianUInt16(bytes, offset + 14);
                if (bitsPerPixel != 1 && bitsPerPixel != 4 &&
                    bitsPerPixel != 8 && bitsPerPixel != 16 &&
                    bitsPerPixel != 24 && bitsPerPixel != 32)
                    return false;
                uint compression =
                    ReadLittleEndianUInt32(bytes, offset + 16);
                if (compression != 0 && compression != 3 &&
                    compression != 6)
                    return false;
            }

            if (width < 1 || rawHeight == 0 ||
                rawHeight == int.MinValue)
                return false;
            int absoluteHeight = Math.Abs(rawHeight);
            if ((absoluteHeight & 1) != 0)
                return false;
            height = absoluteHeight / 2;
            return height > 0;
        }

        private static long DimensionKey(int width, int height)
        {
            return ((long)(uint)width << 32) | (uint)height;
        }

        private static int ReadLittleEndianUInt16(
            byte[] bytes,
            int offset)
        {
            return bytes[offset] | (bytes[offset + 1] << 8);
        }

        private static uint ReadLittleEndianUInt32(
            byte[] bytes,
            int offset)
        {
            return
                (uint)bytes[offset] |
                ((uint)bytes[offset + 1] << 8) |
                ((uint)bytes[offset + 2] << 16) |
                ((uint)bytes[offset + 3] << 24);
        }

        private static int ReadLittleEndianInt32(
            byte[] bytes,
            int offset)
        {
            return unchecked((int)ReadLittleEndianUInt32(bytes, offset));
        }

        private static int ReadBigEndianInt32(byte[] bytes, int offset)
        {
            uint value =
                ((uint)bytes[offset] << 24) |
                ((uint)bytes[offset + 1] << 16) |
                ((uint)bytes[offset + 2] << 8) |
                bytes[offset + 3];
            return value > int.MaxValue ? -1 : (int)value;
        }

        private static void RequireText(string value, int minimum, int maximum, string location)
        {
            int length = value == null ? 0 : value.Length;
            if (length < minimum || length > maximum)
                throw new InvalidDataException(location + " has an invalid length.");
        }
    }

    /// <summary>
    /// Structural MP3 sniffing, lifted out of TSound so a validator can be compiled without the animation
    /// runtime. Three callers reached it -- the runtime, CompanionXmlValidator and SecuritySelfTest -- and the
    /// validator is the one that constrains where it lives: tools/ShimejiConvert recompiles
    /// CompanionXmlValidator.cs to grade converted pets against the app's real rules, and reaching this through
    /// TSound would have dragged Animations.cs and StartUp into an offline converter.
    ///
    /// It sits in THIS file rather than its own on purpose. A separate file has to be registered in every
    /// csproj that compiles the validator (the app, modules/PetStudio and the converter -- EnableDefaultItems
    /// is false everywhere), and touching modules/PetStudio/PetStudio.csproj makes
    /// Test-ModulePublishFreshness mark petstudio.zip stale, forcing a version bump and a user-facing update
    /// prompt for a change with no behavioural effect. Living beside its consumer costs one extra type in
    /// this file and nothing else. Pure move: same bytes checked, same messages.
    /// </summary>
    internal static class Mp3Format
    {
        /// <summary>
        /// Lightweight structural MP3 sanity check (no decode, no NAudio): accept an ID3 tag or an MPEG
        /// audio frame sync. Full decode-validation is the Sound module's job when it plays. This keeps a
        /// cheap gate in the base (rejecting obvious non-audio) without pulling an audio codec into it.
        /// </summary>
        internal static bool LooksLikeMp3(byte[] buff, out string error)
        {
            error = null;
            if (buff == null || buff.Length < 3)
            {
                error = "Sound data is empty or too small.";
                return false;
            }
            // "ID3" tag (0x49 0x44 0x33) marks an MP3 with an ID3v2 header.
            if (buff[0] == 0x49 && buff[1] == 0x44 && buff[2] == 0x33) return true;
            // MPEG audio frame sync: 11 set bits => 0xFF followed by 0xE0..0xFF.
            if (buff.Length >= 2 && buff[0] == 0xFF && (buff[1] & 0xE0) == 0xE0) return true;
            error = "Sound is not a usable MP3 (no ID3 tag or MPEG frame sync).";
            return false;
        }
    }

    /// <summary>
    /// The structural check for a named clip declared <c>format="wav"</c> (grimoire/06 section 3, feature 'wav'): its bytes
    /// must BE a RIFF/WAVE the host decodes, proved from the header and the chunk walk with no decode, as
    /// <see cref="Mp3Format"/> proves an MP3. It lives in this file for the reason given there: no csproj that compiles
    /// the validator has to change.
    ///
    /// What it accepts follows two measurements (feature/shimeji-host-wav). The encodings are those the host's NAudio
    /// decodes with no OS codec, each decoded by --audio-selftest: PCM of 8, 16, 24 or 32 bits and IEEE float of 32 or 64,
    /// plain or WAVE_FORMAT_EXTENSIBLE. ADPCM, mu-law and A-law would need the OS's ACM codecs and are refused, since no
    /// clip of the converter's corpus uses them (all 1168 of its WAVE clips are 8- or 16-bit PCM). And the structure is no
    /// stricter than real clips: chunks after the data chunk, an 18-byte fmt chunk and an odd-sized LAST chunk with no pad
    /// byte all pass, as they do in the source's javax.sound reader. NAudio's own chunk reader never sees a clip: the
    /// decoder plays the fmt and data <see cref="TryParse"/> found (review F1). What is refused is what leaves the
    /// bytes in doubt: a RIFF size that disagrees with the clip (truncated, or trailing bytes), a chunk past the end, no
    /// fmt or no data chunk, an empty or ragged data chunk, a block align that contradicts the channels and bits.
    /// </summary>
    internal static class WavFormat
    {
        /// <summary>The sample rates a clip may have. The lower bound also bounds the decoded size: the mixer resamples to
        /// 44.1 kHz stereo float, so a clip costs at most 44100 x 8 / (8000 x 1) = 44 times its bytes once decoded (8-bit
        /// mono at 8 kHz); a 16-bit stereo 44.1 kHz clip costs twice its bytes. True of what PLAYS because the decoder reads
        /// this parse's fmt (<see cref="TryParse"/>); AudioOutput.MaximumClipDecodedSamples caps it again at decode.</summary>
        public const int MinimumSampleRate = 8000;
        public const int MaximumSampleRate = 192000;

        private const int Pcm = 0x0001;
        private const int IeeeFloat = 0x0003;
        private const int Extensible = 0xFFFE;

        /// <summary>The tail every standard KSDATAFORMAT_SUBTYPE GUID shares after its two-byte format tag.</summary>
        private static readonly byte[] SubtypeGuidTail = { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 };

        /// <summary>Whether a sound declares its bytes WAV. The one reading of the attribute: the validator and the
        /// runtime's staging both ask here, so they cannot disagree about what a document said.</summary>
        internal static bool IsDeclaredWav(XmlData.SoundNode sound)
        {
            return sound != null && string.Equals(sound.Format, "wav", StringComparison.Ordinal);
        }

        /// <summary>Whether a sound carries a <c>format</c> attribute at all, "mp3" included. The one reading of it: what needs
        /// feature 'wav' (ShimejiFormatValidator.CheckDeclaredUsage) and what gets the declared clip's size limit
        /// (CompanionXmlValidator.MaximumAudioBytesFor) ask here, so no clip can have the larger limit without the token
        /// that makes an older host refuse it by name.</summary>
        internal static bool DeclaresFormat(XmlData.SoundNode sound)
        {
            return sound != null && sound.Format != null;
        }

        /// <summary>Whether the bytes open as a RIFF/WAVE form. Used only to word a refusal: the declared format decides
        /// which check runs.</summary>
        internal static bool HasWaveHeader(byte[] b)
        {
            return b != null && b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
                && b[8] == 'W' && b[9] == 'A' && b[10] == 'V' && b[11] == 'E';
        }

        /// <summary>True when the bytes are a RIFF/WAVE this app decodes; otherwise <paramref name="error"/> says why, as a
        /// phrase that follows "but" ("is truncated: ...").</summary>
        internal static bool IsDecodableWave(byte[] b, out string error)
        {
            WaveLayout layout;
            return TryParse(b, out layout, out error);
        }

        /// <summary>What the parse below proved about a clip: the plain format tag (an extensible clip's subformat), its
        /// shape, and where its samples are. The decoder (AudioOutput.DecodeClip) reads exactly this and never parses the
        /// RIFF itself, so what plays is what was validated (review F1, feature/shimeji-host-limits).</summary>
        internal struct WaveLayout
        {
            public int Tag, Channels, SampleRate, BitsPerSample, BlockAlign, DataOffset, DataLength;
        }

        /// <summary>
        /// The ONE parse of a clip's RIFF/WAVE bytes, for the validator and the decoder alike. It takes the FIRST fmt and
        /// the FIRST data chunk and skips an odd chunk's pad byte whatever its value. NAudio's WaveFileReader takes the
        /// LAST of each and skips a pad only when it is zero, so while the decoder handed it the bytes a clip could be
        /// validated under one fmt and decoded under another the validator never saw: a second fmt at 1 Hz, or one hidden
        /// behind a non-zero pad byte, turned a 568-byte clip into 44.1 M samples on the UI thread (the stand-in review,
        /// F1). Refusing duplicates and non-zero pads instead was rejected: that list tracks one reader's internals and
        /// would have to follow them; one parse has nothing to track, and the set it accepts is the one already measured.
        /// </summary>
        internal static bool TryParse(byte[] b, out WaveLayout layout, out string error)
        {
            layout = default(WaveLayout);
            error = null;
            if (!HasWaveHeader(b))
            {
                error = "is not a RIFF/WAVE";
                return false;
            }
            long riffEnd = (long)ReadUInt32(b, 4) + 8;
            if (riffEnd > b.Length)
            {
                error = "is truncated: its RIFF header claims " + riffEnd + " bytes and it holds " + b.Length;
                return false;
            }
            if (riffEnd < b.Length)
            {
                error = "has " + (b.Length - riffEnd) + " bytes after its RIFF form";
                return false;
            }

            long fmt = -1, fmtSize = 0, data = -1, dataSize = 0;
            long at = 12;
            while (at + 8 <= riffEnd)
            {
                long size = ReadUInt32(b, (int)at + 4);
                long body = at + 8;
                if (body + size > riffEnd)
                {
                    error = "is truncated: its '" + ChunkName(b, (int)at) + "' chunk runs past the end";
                    return false;
                }
                if (fmt < 0 && IsChunk(b, (int)at, "fmt ")) { fmt = body; fmtSize = size; }
                else if (data < 0 && IsChunk(b, (int)at, "data")) { data = body; dataSize = size; }
                at = body + size;
                // A chunk of odd size is followed by a pad byte, which a writer may leave off the very last one.
                if ((size & 1) == 1 && at < riffEnd) at++;
            }
            if (at != riffEnd)
            {
                error = "ends in " + (riffEnd - at) + " bytes that are not a chunk";
                return false;
            }
            if (fmt < 0)
            {
                error = "has no fmt chunk";
                return false;
            }
            if (fmtSize < 16)
            {
                error = "has a fmt chunk too short to describe its audio";
                return false;
            }
            int f = (int)fmt;
            int tag = ReadUInt16(b, f), channels = ReadUInt16(b, f + 2), blockAlign = ReadUInt16(b, f + 12), bits = ReadUInt16(b, f + 14);
            long rate = ReadUInt32(b, f + 4);
            if (tag == Extensible)
            {
                // WAVEFORMATEXTENSIBLE: cbSize, valid bits, channel mask, then the subformat GUID at +24.
                if (fmtSize < 40)
                {
                    error = "has an extensible fmt chunk too short for its subformat";
                    return false;
                }
                for (int i = 0; i < SubtypeGuidTail.Length; i++)
                    if (b[f + 26 + i] != SubtypeGuidTail[i])
                    {
                        error = "uses an extensible subformat this app does not decode; a clip is PCM or IEEE float";
                        return false;
                    }
                tag = ReadUInt16(b, f + 24);
            }
            if (tag == Pcm)
            {
                if (bits != 8 && bits != 16 && bits != 24 && bits != 32)
                {
                    error = "is " + bits + "-bit PCM; PCM clips are 8, 16, 24 or 32 bits";
                    return false;
                }
            }
            else if (tag == IeeeFloat)
            {
                if (bits != 32 && bits != 64)
                {
                    error = "is " + bits + "-bit float; float clips are 32 or 64 bits";
                    return false;
                }
            }
            else
            {
                error = "uses WAVE format 0x" + tag.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)
                    + TagName(tag) + ", which this app does not decode; a clip is PCM or IEEE float";
                return false;
            }
            if (channels < 1 || channels > 2)
            {
                error = "has " + channels + " channels; a clip is mono or stereo";
                return false;
            }
            if (rate < MinimumSampleRate || rate > MaximumSampleRate)
            {
                error = "has a sample rate of " + rate + " Hz, outside " + MinimumSampleRate + " to " + MaximumSampleRate;
                return false;
            }
            if (blockAlign != channels * bits / 8)
            {
                error = "has a block align of " + blockAlign + ", which " + channels + " channel(s) of " + bits + " bits contradict";
                return false;
            }
            if (data < 0)
            {
                error = "has no data chunk";
                return false;
            }
            if (dataSize == 0 || dataSize % blockAlign != 0)
            {
                error = dataSize == 0 ? "has no audio (its data chunk is empty)" : "has a data chunk that is not a whole number of frames";
                return false;
            }
            layout = new WaveLayout
            {
                Tag = tag, Channels = channels, SampleRate = (int)rate, BitsPerSample = bits, BlockAlign = blockAlign,
                DataOffset = (int)data, DataLength = (int)dataSize,
            };
            return true;
        }

        private static string TagName(int tag)
        {
            switch (tag)
            {
                case 0x0002: return " (MS ADPCM)";
                case 0x0006: return " (A-law)";
                case 0x0007: return " (mu-law)";
                case 0x0011: return " (IMA ADPCM)";
                case 0x0055: return " (MPEG layer 3)";
                default: return "";
            }
        }

        private static bool IsChunk(byte[] b, int at, string id)
        {
            return b[at] == id[0] && b[at + 1] == id[1] && b[at + 2] == id[2] && b[at + 3] == id[3];
        }

        /// <summary>A chunk id for a message: its four characters when printable, else their hex.</summary>
        private static string ChunkName(byte[] b, int at)
        {
            var name = new StringBuilder(4);
            for (int i = 0; i < 4; i++)
            {
                byte c = b[at + i];
                if (c < 0x20 || c > 0x7E) return BitConverter.ToString(b, at, 4);
                name.Append((char)c);
            }
            return name.ToString();
        }

        private static int ReadUInt16(byte[] b, int at)
        {
            return b[at] | (b[at + 1] << 8);
        }

        private static long ReadUInt32(byte[] b, int at)
        {
            return (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
        }
    }
}
