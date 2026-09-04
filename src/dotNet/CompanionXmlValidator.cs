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
        // downsamples every frame to <=256px on load (ReadImages), so this raises stored file size, NOT per-pet
        // render memory; the 4096px sheet + 16 Mi pixel + 256px frame caps (the memory guard) are unchanged.
        public const int MaximumXmlBytes = 12 * 1024 * 1024;
        public const int MaximumImageBytes = 12 * 1024 * 1024;
        public const int MaximumIconBytes = 512 * 1024;
        public const int MaximumAudioBytesPerSound = 2 * 1024 * 1024;
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
                        "The local pet must be no larger than 12 MiB.");

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
            return TryParse(
                xml,
                out root,
                out error,
                CancellationToken.None);
        }

        public static bool TryParse(
            string xml,
            out XmlData.RootNode root,
            out string error,
            CancellationToken cancellationToken)
        {
            root = null;
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
                    throw new InvalidDataException("Pet XML exceeds the 12 MiB limit.");

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

                Validate(root, cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                root = null;
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
            CancellationToken cancellationToken)
        {
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
                cancellationToken);
            ValidateImage(
                iconBytes,
                1,
                1,
                "pet icon",
                RequiredImageContainer.Icon,
                cancellationToken);

            XmlData.AnimationNode[] animations = root.Animations.Animation;
            if (animations.Length < 1 || animations.Length > MaximumAnimations)
                throw new InvalidDataException("Pet must define 1 to " + MaximumAnimations + " animations.");

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
                if (animation.Start == null || animation.End == null || animation.Sequence == null ||
                    animation.Sequence.Frame == null || animation.Sequence.Frame.Length == 0)
                    throw new InvalidDataException("Animation " + animation.Id + " is incomplete.");

                ValidateMoving(animation.Start, "animation " + animation.Id + " start");
                ValidateMoving(animation.End, "animation " + animation.Id + " end");
                ValidateExpression(animation.Sequence.RepeatCount, "animation " + animation.Id + " repeat");

                framesTotal = checked(framesTotal + animation.Sequence.Frame.Length);
                if (framesTotal > MaximumFramesTotal)
                    throw new InvalidDataException("Pet defines too many animation frames.");
                foreach (int frame in animation.Sequence.Frame)
                    if (frame < 0 || frame >= tileCount)
                        throw new InvalidDataException("Animation " + animation.Id + " references an invalid frame.");

                if (animation.Sequence.RepeatFromFrame < 0 ||
                    animation.Sequence.RepeatFromFrame >= animation.Sequence.Frame.Length)
                    throw new InvalidDataException("Animation " + animation.Id + " has an invalid repeat-from frame.");

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
                    cancellationToken);
                if (animation.Border != null)
                    ValidateNextSet(
                        animation.Border.Next,
                        ids,
                        "animation " + animation.Id + " border",
                        cancellationToken);
                if (animation.Gravity != null)
                    ValidateNextSet(
                        animation.Gravity.Next,
                        ids,
                        "animation " + animation.Id + " gravity",
                        cancellationToken);
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
            foreach (XmlData.SoundNode sound in sounds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sound == null || !ids.Contains(sound.Id) ||
                    sound.Probability < 0 || sound.Probability > 100 ||
                    sound.Loop < 0 || sound.Loop > 20)
                    throw new InvalidDataException("A sound entry is invalid.");
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
                byte[] audioBytes = DecodeBase64(
                    sound.Base64,
                    MaximumAudioBytesPerSound,
                    "sound");
                audioTotal = checked(audioTotal + audioBytes.Length);
                if (audioTotal > MaximumAudioBytesTotal)
                    throw new InvalidDataException("Pet audio exceeds the total size limit.");
                string audioError;
                if (!Mp3Format.LooksLikeMp3(audioBytes, out audioError))
                    throw new InvalidDataException(audioError);
            }
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
            CancellationToken cancellationToken)
        {
            if (nextNodes == null || nextNodes.Length == 0) return;
            if (nextNodes.Length > MaximumTransitions)
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
            if (total <= 0)
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
                    location);

                using (var stream = new MemoryStream(bytes, false))
                using (Image image = Image.FromStream(stream, true, true))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateDimensions(
                        image.Width,
                        image.Height,
                        tilesX,
                        tilesY,
                        location);
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
            string location)
        {
            if (width < 1 || height < 1 ||
                width > MaximumImageDimension || height > MaximumImageDimension ||
                (long)width * height > MaximumImagePixels ||
                width % tilesX != 0 || height % tilesY != 0)
                throw new InvalidDataException(
                    location + " has invalid dimensions or tile geometry.");
            if ((width / tilesX) > MaximumSpriteFrameDimension ||
                (height / tilesY) > MaximumSpriteFrameDimension)
                throw new InvalidDataException(
                    location + " contains a frame larger than the runtime supports.");
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
                    location);
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
}
