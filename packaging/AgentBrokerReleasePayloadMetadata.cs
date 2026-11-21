#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

// Product release validation. Never execute target code or print arbitrary metadata.
// Only ModuleKit's required embedded PDB metadata is inspected; no SourceLink/source blob is decoded.
public static class AgentBrokerReleasePayloadMetadata
{
    private const string SourceKey = "AgentBroker.SourceContentSha256";
    private const string ParentKey = "AgentBroker.SourceParentCommit";
    private static readonly string[] Owned =
    [
        "AgentBroker.dll", "DesktopAICompanion.ModuleKit.dll", "worker/AgentBroker.Worker.dll",
        "worker/AgentBroker.Core.dll", "worker/AgentBroker.CodexDesktopAdapter.dll",
        "worker/AgentBroker.ClaudeAdapter.dll"
    ];
    private static void Require(bool ok, string label, string relative = "")
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + label + (relative.Length == 0 ? "" : ": " + relative));
        if (!ok) throw new CheckFailure();
    }
    public static int Validate(string root, string manifest, string source, string parent)
        => Main(new[] { root, manifest, source, parent });

    // Shared future-package boundary. No AgentBroker source/parent stamp is required here.
    public static int ValidateModuleKitOnly(string root)
    {
        const string relative = "DesktopAICompanion.ModuleKit.dll";
        try
        {
            string full = Path.GetFullPath(root);
            Require(Directory.Exists(full), "PACKAGE_MODULEKIT_ROOT_REQUIRED");
            Plain(full, relative);
            string path = Path.Combine(full, relative);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException)
            {
                Console.WriteLine("NOT_APPLICABLE PACKAGE_MODULEKIT_SYMBOLS: no ModuleKit payload");
                return 0;
            }
            Require((attributes & FileAttributes.Directory) == 0, "PACKAGE_MODULEKIT_FILE_REQUIRED", relative);
            Plain(path, relative);
            using var input = File.OpenRead(path);
            using var pe = new PEReader(input);
            Require(pe.HasMetadata, "PACKAGE_MODULEKIT_MANAGED_METADATA", relative);
            var metadata = pe.GetMetadataReader();
            Require(metadata.GetString(metadata.GetAssemblyDefinition().Name) == "DesktopAICompanion.ModuleKit",
                "PACKAGE_MODULEKIT_ASSEMBLY_NAME", relative);
            CheckModuleKitSymbols(pe, pe.ReadDebugDirectory(), relative);
            Console.WriteLine("PASS PACKAGE_MODULEKIT_SYMBOLS");
            return 0;
        }
        catch (CheckFailure) { return 1; }
        catch { Console.WriteLine("FAIL PACKAGE_MODULEKIT_SYMBOLS_UNEXPECTED_EXCEPTION"); return 2; }
    }


    private static int Main(string[] args)
    {
        var Rows = new List<object>();
        try
        {
            Require(args.Length == 4, "RELEASE_CHECK_ARGUMENTS");
            string root = Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar);
            string manifest = Path.GetFullPath(args[1]);
            string source = args[2], parent = args[3];
            Require(source.Length == 64 && Regex.IsMatch(source, "^[A-F0-9]{64}$") &&
                parent.Length == 40 && Regex.IsMatch(parent, "^[a-f0-9]{40}$"), "RELEASE_CHECK_IDENTITY_FORMAT");
            using var record = JsonDocument.Parse(File.ReadAllBytes(manifest));
            Require(record.RootElement.GetProperty("sourceContentSha256").GetString() == source &&
                record.RootElement.GetProperty("parentCommit").GetString() == parent, "RELEASE_CHECK_MANIFEST_BINDING");
            string? workerVersion = null;
            foreach (string relative in Owned)
            {
                string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Require(File.Exists(path), "RELEASE_OWNED_ASSEMBLY_PRESENT", relative);
                Plain(path, relative);
                using var input = File.OpenRead(path);
                using var pe = new PEReader(input);
                Require(pe.HasMetadata, "RELEASE_OWNED_MANAGED_METADATA", relative);
                bool moduleKit = relative == "DesktopAICompanion.ModuleKit.dll";
                var debug = pe.ReadDebugDirectory();
                if (moduleKit) CheckModuleKitSymbols(pe, debug, relative);
                else Require(!debug.Any(e => e.Type is DebugDirectoryEntryType.CodeView or DebugDirectoryEntryType.EmbeddedPortablePdb),
                    "RELEASE_NO_PROJECT_DEBUG_RECORDS", relative);
                Require(!File.Exists(Path.ChangeExtension(path, ".pdb")), "RELEASE_NO_PROJECT_PDB_SIDECAR", relative);
                var metadata = pe.GetMetadataReader();
                Require(metadata.GetString(metadata.GetAssemblyDefinition().Name) == Path.GetFileNameWithoutExtension(relative),
                    "RELEASE_OWNED_ASSEMBLY_NAME_MATCH", relative);
                var sources = new List<string?>();
                var parents = new List<string?>();
                var versions = new List<string?>();
                bool optimizationsEnabled = true;
                foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
                {
                    var attribute = metadata.GetCustomAttribute(handle);
                    string type = AttributeType(metadata, attribute.Constructor);
                    if (type == "System.Diagnostics.DebuggableAttribute")
                    {
                        optimizationsEnabled &= !DebuggableDisablesOptimizations(metadata, attribute, relative);
                        continue;
                    }
                    if (type is not ("System.Reflection.AssemblyMetadataAttribute" or "System.Reflection.AssemblyInformationalVersionAttribute")) continue;
                    var blob = metadata.GetBlobReader(attribute.Value);
                    Require(blob.ReadUInt16() == 1, "RELEASE_ATTRIBUTE_PROLOG", relative);
                    if (type == "System.Reflection.AssemblyInformationalVersionAttribute") versions.Add(blob.ReadSerializedString());
                    else
                    {
                        // Ignore arbitrary metadata values. Read only the public provenance keys.
                        string? key = blob.ReadSerializedString();
                        if (key == SourceKey) sources.Add(blob.ReadSerializedString());
                        else if (key == ParentKey) parents.Add(blob.ReadSerializedString());
                    }
                }
                Require(optimizationsEnabled, "RELEASE_OWNED_OPTIMIZATIONS_ENABLED", relative);
                Require(versions.Count == 1 && versions[0] is { Length: > 0 and <= 256 } &&
                    !Regex.IsMatch(versions[0]!, "[+][a-fA-F0-9]{7,64}$"), "RELEASE_NO_GIT_SOURCE_SUFFIX", relative);
                bool entryAssembly = relative is "AgentBroker.dll" or "worker/AgentBroker.Worker.dll";
                if (entryAssembly)
                {
                    Require(sources.Count == 1 && sources[0] == source, "RELEASE_SOURCE_CONTENT_MATCH", relative);
                    Require(parents.Count == 1 && parents[0] == parent, "RELEASE_PARENT_COMMIT_MATCH", relative);
                }
                if (relative == "worker/AgentBroker.Worker.dll") workerVersion = versions[0];
                Rows.Add(new { file = relative, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    noProjectDebugRecords = !moduleKit, requiredMappedEmbeddedSymbols = moduleKit,
                    noProjectPdbSidecar = true, provenanceChecked = entryAssembly });
            }
            string apphost = Path.Combine(root, "worker", "AgentBroker.Worker.exe");
            Plain(apphost, "worker/AgentBroker.Worker.exe");
            Require(File.Exists(apphost) && workerVersion is not null && FileVersionInfo.GetVersionInfo(apphost).ProductVersion == workerVersion,
                "RELEASE_WORKER_APPHOST_VERSION_MATCH", "worker/AgentBroker.Worker.exe");
            Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", sourceContentSha256 = source, parentCommit = parent, files = Rows,
                scope = "Six owned managed assemblies and Worker apphost; ModuleKit embedded metadata checked without printing document names or decoding SourceLink/source blobs; no target code executed." }));
            return 0;
        }
        catch (CheckFailure) { return 1; }
        catch { Console.WriteLine("FAIL RELEASE_CHECK_UNEXPECTED_EXCEPTION"); return 2; }
    }
    // Decode the two actual BCL constructors; PDB absence is not proof of optimization.
    private static bool DebuggableDisablesOptimizations(MetadataReader metadata, CustomAttribute attribute, string relative)
    {
        BlobHandle signature;
        string constructor;
        if (attribute.Constructor.Kind == HandleKind.MemberReference)
        {
            var member = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            signature = member.Signature; constructor = metadata.GetString(member.Name);
        }
        else if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
        {
            var method = metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
            signature = method.Signature; constructor = metadata.GetString(method.Name);
        }
        else { Require(false, "RELEASE_DEBUGGABLE_CONSTRUCTOR", relative); return true; }
        var parameters = metadata.GetBlobReader(signature);
        Require(constructor == ".ctor" && parameters.ReadByte() == 0x20,
            "RELEASE_DEBUGGABLE_CONSTRUCTOR", relative); // instance, default calling convention
        int count = parameters.ReadCompressedInteger();
        Require(parameters.ReadByte() == 0x01, "RELEASE_DEBUGGABLE_SIGNATURE", relative); // void
        bool booleans = count == 2;
        if (booleans)
            Require(parameters.ReadByte() == 0x02 && parameters.ReadByte() == 0x02,
                "RELEASE_DEBUGGABLE_SIGNATURE", relative);
        else
        {
            Require(count == 1 && parameters.ReadByte() == 0x11, "RELEASE_DEBUGGABLE_SIGNATURE", relative); // valuetype
            Require(IsDebuggingModes(metadata, parameters.ReadCompressedInteger()), "RELEASE_DEBUGGABLE_ENUM_TYPE", relative);
        }
        Require(parameters.RemainingBytes == 0, "RELEASE_DEBUGGABLE_SIGNATURE", relative);
        var value = metadata.GetBlobReader(attribute.Value);
        Require(value.ReadUInt16() == 1, "RELEASE_DEBUGGABLE_VALUE", relative);
        bool disabled;
        if (booleans)
        {
            byte tracking = value.ReadByte(), optimizationDisabled = value.ReadByte();
            Require(tracking <= 1 && optimizationDisabled <= 1, "RELEASE_DEBUGGABLE_VALUE", relative);
            disabled = optimizationDisabled != 0;
        }
        else disabled = (value.ReadInt32() & 0x100) != 0; // DebuggingModes.DisableOptimizations
        Require(value.ReadUInt16() == 0 && value.RemainingBytes == 0, "RELEASE_DEBUGGABLE_VALUE", relative);
        return disabled;
    }
    private static bool IsDebuggingModes(MetadataReader metadata, int codedType)
    {
        int row = codedType >> 2;
        if (row <= 0) return false;
        if ((codedType & 3) == 1)
        {
            var type = metadata.GetTypeReference(System.Reflection.Metadata.Ecma335.MetadataTokens.TypeReferenceHandle(row));
            if (metadata.GetString(type.Name) != "DebuggingModes" || type.ResolutionScope.Kind != HandleKind.TypeReference) return false;
            var parent = metadata.GetTypeReference((TypeReferenceHandle)type.ResolutionScope);
            return Name(metadata, parent.Namespace, parent.Name) == "System.Diagnostics.DebuggableAttribute";
        }
        if ((codedType & 3) == 0)
        {
            var type = metadata.GetTypeDefinition(System.Reflection.Metadata.Ecma335.MetadataTokens.TypeDefinitionHandle(row));
            var declaring = type.GetDeclaringType();
            if (metadata.GetString(type.Name) != "DebuggingModes" || declaring.IsNil) return false;
            var parent = metadata.GetTypeDefinition(declaring);
            return Name(metadata, parent.Namespace, parent.Name) == "System.Diagnostics.DebuggableAttribute";
        }
        return false;
    }

    private static void CheckModuleKitSymbols(PEReader pe, System.Collections.Immutable.ImmutableArray<DebugDirectoryEntry> entries, string relative)
    {
        var embedded = entries.Where(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb).ToArray();
        Require(embedded.Length == 1, "RELEASE_MODULEKIT_EMBEDDED_PDB_REQUIRED", relative);
        foreach (var entry in entries.Where(e => e.Type == DebugDirectoryEntryType.CodeView))
        {
            string value = pe.ReadCodeViewDebugDirectoryData(entry).Path;
            bool mapped = value.StartsWith("/_/", StringComparison.Ordinal) && !value.Contains(':');
            Require(value == "DesktopAICompanion.ModuleKit.pdb" || mapped,
                "RELEASE_MODULEKIT_CODEVIEW_PATH", relative);
        }
        using var provider = pe.ReadEmbeddedPortablePdbDebugDirectoryData(embedded[0]);
        var metadata = provider.GetMetadataReader();
        Require(metadata.Documents.Count > 0, "RELEASE_MODULEKIT_DOCUMENTS_REQUIRED", relative);
        bool mappedDocuments = true, sourceLinkAbsent = true, embeddedSourceAbsent = true;
        foreach (var handle in metadata.Documents)
        {
            string name = metadata.GetString(metadata.GetDocument(handle).Name);
            mappedDocuments &= name.StartsWith("/_/", StringComparison.Ordinal) && !name.Contains(':');
        }
        var sourceLink = new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A");
        var embeddedSource = new Guid("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
        foreach (var handle in metadata.CustomDebugInformation)
        {
            Guid kind = metadata.GetGuid(metadata.GetCustomDebugInformation(handle).Kind);
            sourceLinkAbsent &= kind != sourceLink;
            embeddedSourceAbsent &= kind != embeddedSource;
        }
        Require(mappedDocuments, "RELEASE_MODULEKIT_DOCUMENT_PATHS_MAPPED", relative);
        Require(sourceLinkAbsent, "RELEASE_MODULEKIT_SOURCELINK_ABSENT", relative);
        Require(embeddedSourceAbsent, "RELEASE_MODULEKIT_EMBEDDED_SOURCE_ABSENT", relative);
    }

    private static void Plain(string path, string relative)
    {
        for (string? cursor = Path.GetFullPath(path); cursor is not null; cursor = Path.GetDirectoryName(cursor))
            if (File.Exists(cursor) || Directory.Exists(cursor))
                Require((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, "RELEASE_CHECK_PLAIN_PATH", relative);
    }
    private static string AttributeType(MetadataReader metadata, EntityHandle constructor)
    {
        EntityHandle declaring = constructor.Kind switch
        {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default
        };
        return declaring.Kind switch
        {
            HandleKind.TypeReference => Name(metadata, metadata.GetTypeReference((TypeReferenceHandle)declaring).Namespace,
                metadata.GetTypeReference((TypeReferenceHandle)declaring).Name),
            HandleKind.TypeDefinition => Name(metadata, metadata.GetTypeDefinition((TypeDefinitionHandle)declaring).Namespace,
                metadata.GetTypeDefinition((TypeDefinitionHandle)declaring).Name),
            _ => ""
        };
    }
    private static string Name(MetadataReader metadata, StringHandle ns, StringHandle name) => metadata.GetString(ns) + "." + metadata.GetString(name);
    private sealed class CheckFailure : Exception { }
}
