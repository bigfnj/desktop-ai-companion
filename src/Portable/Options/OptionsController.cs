using System;
using System.Collections.Generic;

namespace DesktopAICompanion.Options
{
    // =====================================================================================
    // Renderer-agnostic controller layer ("the seam") for the Pets pane. NOTHING here references
    // System.Windows.Forms: a WPF view binds to the State DTOs and calls the command methods. All
    // validation/clamping lives here so every renderer behaves identically. The layer is `internal`
    // because the domain services it wraps are internal; it compiles into the DesktopAICompanion exe.
    //
    // The Preferences/Fortunes/AI controllers + the OptionsController façade + OptionsSelfTest were
    // removed with the residual base fortune/AI-brain engines; only the live CompanionsController remains
    // (used by the WPF Pets pane). Its shared result/runtime/catalog seam types stay alongside it.
    // =====================================================================================

    // ---- shared result (mirrors the existing Set*(...) -> bool + rollback pattern) ----
    internal class OpResult
    {
        public bool Ok;
        public string Message;
        public static OpResult Success(string m = null) { return new OpResult { Ok = true, Message = m }; }
        public static OpResult Fail(string m) { return new OpResult { Ok = false, Message = m }; }
    }
    // Seam over StartUp/Program.Mainthread so controllers don't bind the WinForms singleton and are
    // fakeable in tests. StartUp implements this (its methods already exist).
    //
    // Exactly the members the controller calls THROUGH it (RA-311). IsAtMaxPets and RemoveOnePet were
    // declared here and reached only on the concrete StartUp (ContextMenus, CompanionHost, the Companions
    // pane's Remove button), and ReloadAiSettings only through a cast of the public method on the same
    // object; a seam that declares what nobody routes through it reads as a fake-able surface it is not.
    // The two panes still bind Program.Mainthread directly for those, and UsePet still reads Program.MyData
    // for the active-id write: finishing the seam means injecting the store too, which is a refactor of
    // the pane's construction and not this lane's call. No test fakes this interface today.
    internal interface ICompanionRuntime
    {
        /// <summary>The id of the active pet, already normalised (an absent or unsafe value becomes the
        /// built-in). This replaced ActivePetXml, which forced the Companions pane to read and compare
        /// every installed pet's whole document to answer a question about identity.</summary>
        string ActivePetId { get; }
        bool LoadNewXMLFromString(string xml);              // replace-all ("Use this companion")
        bool AddPetFromTray(string id);                     // add-alongside
    }

    // =============================== PETS ===============================
    internal sealed class CompanionRow { public string Id; public string DisplayName; public bool IsBuiltIn; public bool IsActive; }
    internal sealed class CompanionsState { public List<CompanionRow> Installed = new List<CompanionRow>(); }

    internal sealed class CompanionsController
    {
        private readonly ICompanionRuntime _runtime;
        public CompanionsState State { get; private set; }

        public CompanionsController(ICompanionRuntime runtime) { _runtime = runtime; }

        public void Load()
        {
            State = new CompanionsState();
            // The ID, not the whole document. IsActive used to re-read every installed pet's XML and
            // string-compare it against the active one: with the full catalog that is roughly 10 MB of
            // synchronous reads (158 KB for esheep64, 406 KB for hornet, 54 pets), and Load runs from the
            // control's constructor, which is rebuilt on every pane selection and after every button press.
            //
            // It is also the more CORRECT question. Every other part of the app already asks it this way
            // -- ContextMenus, CompanionHost and three places in StartUp all read GetActivePetId -- so a
            // pet whose file was edited on disk since it was selected used to drop out of "active" in this
            // one pane while staying active everywhere else.
            string activeId = _runtime != null ? _runtime.ActivePetId : null;
            foreach (CompanionCatalog.CompanionInfo p in CompanionCatalog.EnumerateLocal())
                State.Installed.Add(new CompanionRow { Id = p.Id, DisplayName = p.DisplayName, IsBuiltIn = p.IsBuiltIn, IsActive = IsActive(p, activeId) });
        }

        public OpResult UsePet(string petId)
        {
            // Load tolerates a null runtime (a headless test builds the controller without one); the two
            // commands used to dereference it a few lines later (RA-311). A refusal with a reason, in the
            // same OpResult the pane already shows.
            if (_runtime == null) return OpResult.Fail("No running companion host to apply it to.");
            string xml, err;
            if (!CompanionCatalog.TryReadPetXml(petId, out xml, out err)) return OpResult.Fail(err);

            // Record which pet is now active so per-pet size/sound key by its real id (normalize handles ""/built-in).
            //
            // WRITTEN BEFORE THE APPLY, AND PUT BACK IF THE APPLY FAILS. LoadNewXMLFromString returns
            // false on three real paths -- disposed, TryStageRuntime rejecting the XML, and its own
            // catch -- and that catch already restores the previous XML, images and icon. The id was
            // the one thing it did not restore, so a failed Use left settings.json naming a companion
            // that is not running: the pane said "Couldn't apply that companion" while moving the
            // ACTIVE marker onto it, and the next launch staged the old pet's XML under the new pet's
            // id, so per-pet size, sound mute, monitor pin and speech source all resolved against the
            // wrong companion from then on.
            string previousId = Program.MyData != null ? Program.MyData.GetActivePetId() : null;
            if (Program.MyData != null) Program.MyData.SetActivePetId(petId);

            bool ok = _runtime.LoadNewXMLFromString(xml);
            if (!ok && Program.MyData != null) Program.MyData.SetActivePetId(previousId);
            if (ok) Load();
            return ok ? OpResult.Success("Companion applied.") : OpResult.Fail("Couldn't apply companion.");
        }
        public OpResult AddPet(string petId)
        {
            if (_runtime == null) return OpResult.Fail("No running companion host to add it to.");
            bool ok = _runtime.AddPetFromTray(string.IsNullOrEmpty(petId) ? CompanionCatalog.BuiltInPetId : petId);
            return ok ? OpResult.Success("Added.") : OpResult.Fail("Max companions reached or load failed.");
        }
        internal static bool IsActive(CompanionCatalog.CompanionInfo p, string activeId)
        {
            if (string.IsNullOrEmpty(activeId)) return false;
            string id = p.IsBuiltIn ? CompanionCatalog.BuiltInPetId : p.Id;
            // Ordinal-ignore-case: the id is a folder name on Windows, and the active id is stored
            // normalised but was typed by whatever wrote it.
            return string.Equals(id, activeId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
