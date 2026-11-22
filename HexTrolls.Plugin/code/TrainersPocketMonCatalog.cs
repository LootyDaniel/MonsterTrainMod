// TrainersPocketMonCatalog.cs
// Optional catalog for code that needs to enumerate the 39 new cards.
// Data-driven evolution and actual effects are in TrainersGen1Pokemon.json.
// Safe to add to your existing project: no Harmony hooks, no asset registrations.
using System;
using System.Collections.Generic;
namespace HexTrolls.Plugin.code
{
    public static class TrainersPocketMonCatalog
    {
        public const string ClanId = "TrainersClan";
        public const string SubtypeId = "TrainersPocketMon";
        public const string CardPoolId = "TrainersPokemonPool";
        public sealed class Form
        {
            public readonly string Line;
            public readonly int Stage;
            public readonly string CardId;
            public readonly string NextCardId;
            public readonly int EvolveLevel;
            public Form(string line, int stage, string cardId, string nextCardId, int evolveLevel)
            { Line=line; Stage=stage; CardId=cardId; NextCardId=nextCardId; EvolveLevel=evolveLevel; }
        }
        private static readonly Form[] AllFormsInternal = new Form[]
        {
            new Form("Cuttlfish", 1, "TrainersPocketMonCuttlfishForm1Card", "TrainersPocketMonCuttlfishForm2Card", 16),
            new Form("Cuttlfish", 2, "TrainersPocketMonCuttlfishForm2Card", "TrainersPocketMonCuttlfishForm3Card", 32),
            new Form("Cuttlfish", 3, "TrainersPocketMonCuttlfishForm3Card", null, 0),
            new Form("Hyena", 1, "TrainersPocketMonHyenaForm1Card", "TrainersPocketMonHyenaForm2Card", 22),
            new Form("Hyena", 2, "TrainersPocketMonHyenaForm2Card", null, 0),
            new Form("BloodWorm", 1, "TrainersPocketMonBloodWormForm1Card", "TrainersPocketMonBloodWormForm2Card", 22),
            new Form("BloodWorm", 2, "TrainersPocketMonBloodWormForm2Card", null, 0),
            new Form("Snapsaurus", 1, "TrainersPocketMonSnapsaurusForm1Card", "TrainersPocketMonSnapsaurusForm2Card", 16),
            new Form("Snapsaurus", 2, "TrainersPocketMonSnapsaurusForm2Card", "TrainersPocketMonSnapsaurusForm3Card", 32),
            new Form("Snapsaurus", 3, "TrainersPocketMonSnapsaurusForm3Card", null, 0),
            new Form("Teslasaurus", 1, "TrainersPocketMonTeslasaurusForm1Card", "TrainersPocketMonTeslasaurusForm2Card", 16),
            new Form("Teslasaurus", 2, "TrainersPocketMonTeslasaurusForm2Card", "TrainersPocketMonTeslasaurusForm3Card", 32),
            new Form("Teslasaurus", 3, "TrainersPocketMonTeslasaurusForm3Card", null, 0),
            new Form("Garhound", 1, "TrainersPocketMonGarhoundForm1Card", "TrainersPocketMonGarhoundForm2Card", 16),
            new Form("Garhound", 2, "TrainersPocketMonGarhoundForm2Card", "TrainersPocketMonGarhoundForm3Card", 32),
            new Form("Garhound", 3, "TrainersPocketMonGarhoundForm3Card", null, 0),
            new Form("Fireary", 1, "TrainersPocketMonFirearyForm1Card", null, 0),
            new Form("Frosset", 1, "TrainersPocketMonFrossetForm1Card", "TrainersPocketMonFrossetForm2Card", 16),
            new Form("Frosset", 2, "TrainersPocketMonFrossetForm2Card", "TrainersPocketMonFrossetForm3Card", 32),
            new Form("Frosset", 3, "TrainersPocketMonFrossetForm3Card", null, 0),
            new Form("Umbrabeak", 1, "TrainersPocketMonUmbrabeakForm1Card", "TrainersPocketMonUmbrabeakForm2Card", 16),
            new Form("Umbrabeak", 2, "TrainersPocketMonUmbrabeakForm2Card", "TrainersPocketMonUmbrabeakForm3Card", 32),
            new Form("Umbrabeak", 3, "TrainersPocketMonUmbrabeakForm3Card", null, 0),
            new Form("Magmoth", 1, "TrainersPocketMonMagmothForm1Card", "TrainersPocketMonMagmothForm2Card", 22),
            new Form("Magmoth", 2, "TrainersPocketMonMagmothForm2Card", null, 0),
            new Form("Chilload", 1, "TrainersPocketMonChilloadForm1Card", null, 0),
            new Form("DragonMoth", 1, "TrainersPocketMonDragonMothForm1Card", "TrainersPocketMonDragonMothForm2Card", 16),
            new Form("DragonMoth", 2, "TrainersPocketMonDragonMothForm2Card", "TrainersPocketMonDragonMothForm3Card", 32),
            new Form("DragonMoth", 3, "TrainersPocketMonDragonMothForm3Card", null, 0),
            new Form("Leafshrew", 1, "TrainersPocketMonLeafshrewForm1Card", "TrainersPocketMonLeafshrewForm2Card", 22),
            new Form("Leafshrew", 2, "TrainersPocketMonLeafshrewForm2Card", null, 0),
            new Form("Manshrimp", 1, "TrainersPocketMonManshrimpForm1Card", "TrainersPocketMonManshrimpForm2Card", 16),
            new Form("Manshrimp", 2, "TrainersPocketMonManshrimpForm2Card", "TrainersPocketMonManshrimpForm3Card", 32),
            new Form("Manshrimp", 3, "TrainersPocketMonManshrimpForm3Card", null, 0),
            new Form("Jourmancoil", 1, "TrainersPocketMonJourmancoilForm1Card", "TrainersPocketMonJourmancoilForm2Card", 22),
            new Form("Jourmancoil", 2, "TrainersPocketMonJourmancoilForm2Card", null, 0),
            new Form("Vampirant", 1, "TrainersPocketMonVampirantForm1Card", "TrainersPocketMonVampirantForm2Card", 16),
            new Form("Vampirant", 2, "TrainersPocketMonVampirantForm2Card", "TrainersPocketMonVampirantForm3Card", 32),
            new Form("Vampirant", 3, "TrainersPocketMonVampirantForm3Card", null, 0),
        };
        public static IReadOnlyList<Form> AllForms { get { return AllFormsInternal; } }
        public static bool TryFind(string shortCardId, out Form form)
        {
            form = null;
            if (String.IsNullOrEmpty(shortCardId)) return false;
            // Also accept a full Trainworks runtime-prefixed Card ID.
            foreach (Form candidate in AllFormsInternal)
            {
                if (String.Equals(candidate.CardId, shortCardId, StringComparison.OrdinalIgnoreCase) ||
                    shortCardId.EndsWith("-Card-" + candidate.CardId, StringComparison.OrdinalIgnoreCase))
                { form = candidate; return true; }
            }
            return false;
        }
        public static bool TryGetEvolution(string shortCardId, out string nextCardId, out int level)
        {
            nextCardId=null; level=0;
            Form form;
            if (!TryFind(shortCardId, out form) || String.IsNullOrEmpty(form.NextCardId)) return false;
            nextCardId=form.NextCardId; level=form.EvolveLevel; return true;
        }
    }
}
