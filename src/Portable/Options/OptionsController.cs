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
    internal interface ICompanionRuntime
    {
        string ActivePetXml { get; }
        bool IsAtMaxPets { get; }
        bool LoadNewXMLFromString(string xml);              // replace-all ("Use this companion")
        bool AddPetFromTray(string id);                     // add-alongside
        bool RemoveOnePet(string id);
        void ReloadAiSettings();
    }

    // =============================== PETS ===============================
    internal sealed class CompanionRow { public string Id; public string DisplayName; public bool IsBuiltIn; public bool IsActive; }
    internal sealed class CompanionsState { public List<CompanionRow> Installed = new List<CompanionRow>(); }

    internal sealed class CompanionsController
    {
        private readonly ICompanionRuntime _runtime;
        public CompanionsState State { get; private set; }
        public event Action PetsChanged;

        public CompanionsController(ICompanionRuntime runtime) { _runtime = runtime; }

        public void Load()
        {
            State = new CompanionsState();
            string activeXml = _runtime != null ? _runtime.ActivePetXml : null;
            foreach (CompanionCatalog.CompanionInfo p in CompanionCatalog.EnumerateLocal())
                State.Installed.Add(new CompanionRow { Id = p.Id, DisplayName = p.DisplayName, IsBuiltIn = p.IsBuiltIn, IsActive = IsActive(p, activeXml) });
        }

        public OpResult UsePet(string petId)
        {
            string xml, err;
            if (!CompanionCatalog.TryReadPetXml(petId, out xml, out err)) return OpResult.Fail(err);
            // Record which pet is now active so per-pet size/sound key by its real id (normalize handles ""/built-in).
            if (Program.MyData != null) Program.MyData.SetActivePetId(petId);
            bool ok = _runtime.LoadNewXMLFromString(xml);
            if (ok) { Load(); Raise(); }
            return ok ? OpResult.Success("Companion applied.") : OpResult.Fail("Couldn't apply companion.");
        }
        public OpResult AddPet(string petId)
        {
            bool ok = _runtime.AddPetFromTray(string.IsNullOrEmpty(petId) ? CompanionCatalog.BuiltInPetId : petId);
            if (ok) Raise();
            return ok ? OpResult.Success("Added.") : OpResult.Fail("Max companions reached or load failed.");
        }
        private void Raise() { var h = PetsChanged; if (h != null) h(); }
        private static bool IsActive(CompanionCatalog.CompanionInfo p, string activeXml)
        {
            if (string.IsNullOrEmpty(activeXml)) return false;
            string xml, err;
            if (!CompanionCatalog.TryReadPetXml(p.IsBuiltIn ? CompanionCatalog.BuiltInPetId : p.Id, out xml, out err)) return false;
            return string.Equals(xml, activeXml, StringComparison.Ordinal);
        }
    }
}
