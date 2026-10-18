using KL.Utils;

namespace ExpandedTraitsMod {
    internal static class ExpandedTraitIds {
        internal const string AnimalPerson = "AnimalPerson";
        internal const string Introvert = "Introvert";
        internal const string Extrovert = "Extrovert";
        internal const string Empath = "Empath";
        internal const string LightSleeper = "LightSleeper";
        internal const string HeavySleeper = "HeavySleeper";
        internal const string IronStomach = "IronStomach";
        internal const string SensitiveStomach = "SensitiveStomach";
        internal const string Claustrophobic = "Claustrophobic";
        internal const string Daredevil = "Daredevil";
        internal const string Cautious = "Cautious";
        internal const string HeatAdapted = "HeatAdapted";
        internal const string ColdAdapted = "ColdAdapted";
        internal const string PetPrefix = "ExpandedTraits.PetAffinity.";
        internal const string Beautiful = "Beautiful";
        internal const string Handsome = "Handsome";
        internal const string Ugly = "Ugly";
        internal const string Carnivore = "Carnivore";
        internal const string Vegan = "Vegan";
        internal const string CoffeeAddict = "CoffeeAddict";
        internal const string Minimalist = "Minimalist";
        internal const string Curious = "Curious";
        internal const string Paranoid = "Paranoid";

        internal static readonly int AnimalPersonH = Hashes.S(AnimalPerson);
        internal static readonly int IntrovertH = Hashes.S(Introvert);
        internal static readonly int ExtrovertH = Hashes.S(Extrovert);
        internal static readonly int EmpathH = Hashes.S(Empath);
        internal static readonly int LightSleeperH = Hashes.S(LightSleeper);
        internal static readonly int HeavySleeperH = Hashes.S(HeavySleeper);
        internal static readonly int IronStomachH = Hashes.S(IronStomach);
        internal static readonly int SensitiveStomachH = Hashes.S(SensitiveStomach);
        internal static readonly int ClaustrophobicH = Hashes.S(Claustrophobic);
        internal static readonly int DaredevilH = Hashes.S(Daredevil);
        internal static readonly int CautiousH = Hashes.S(Cautious);
        internal static readonly int HeatAdaptedH = Hashes.S(HeatAdapted);
        internal static readonly int ColdAdaptedH = Hashes.S(ColdAdapted);
        internal static readonly int BeautifulH = Hashes.S(Beautiful);
        internal static readonly int HandsomeH = Hashes.S(Handsome);
        internal static readonly int UglyH = Hashes.S(Ugly);
        internal static readonly int CarnivoreH = Hashes.S(Carnivore);
        internal static readonly int VeganH = Hashes.S(Vegan);
        internal static readonly int CoffeeAddictH = Hashes.S(CoffeeAddict);
        internal static readonly int MinimalistH = Hashes.S(Minimalist);
        internal static readonly int CuriousH = Hashes.S(Curious);
        internal static readonly int ParanoidH = Hashes.S(Paranoid);

        internal static bool IsGeneratedPetTrait(string id) =>
            id != null && id.StartsWith(PetPrefix, System.StringComparison.Ordinal);

        internal static string PetTraitId(string speciesId) => PetPrefix + speciesId;
    }
}
