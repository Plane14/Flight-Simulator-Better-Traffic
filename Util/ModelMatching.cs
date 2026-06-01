using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;
using Simvars.Model;

namespace Simvars.Util
{
    public static class ModelMatching
    {
        public static string MatchModel(Aircraft aircraft, List<Addon> addons = null)
        {
            List<Addon> installedAddons = addons ?? AddonScanner.ScanAddons();

            // Guard against missing/short codes so matching never throws on sparse FR24 data.
            if (string.IsNullOrWhiteSpace(aircraft.model)) aircraft.model = "Airbus A320 Neo";
            if (string.IsNullOrWhiteSpace(aircraft.modelCode)) aircraft.modelCode = "A320";

            string shortModel = aircraft.shortModel;
            string shorterCode = aircraft.shorterModelCode;
            string categoryDefault = CategoryDefault(aircraft);

            Log.Information($"Model matching: {aircraft.model} with airline: {aircraft.airline}, airline ICAO Code: {aircraft.icaoAirline} and modelCode {aircraft.modelCode}");
            JObject models = JObject.Parse(File.ReadAllText(@".\Config\ModelMatching.json"));
            string matchedModel = (string)models.GetValue(aircraft.model) ?? (string)models.GetValue(aircraft.modelCode) ?? installedAddons.FirstOrDefault(addon => ((addon.ModelCode == aircraft.modelCode || TitleContains(addon, shortModel) || TitleContains(addon, shorterCode)) && addon.Icao_Airline == "") && addon.BaseAircraft)?.Title ?? installedAddons.FirstOrDefault(addon => (addon.ModelCode == aircraft.modelCode || TitleContains(addon, shortModel) || TitleContains(addon, shorterCode)) && addon.Icao_Airline == "")?.Title ?? installedAddons.FirstOrDefault(addon => CodeContains(addon, shorterCode) || TitleContains(addon, shorterCode))?.Title ?? (string)models.GetValue("Default Aircraft") ?? categoryDefault;

            matchedModel = (matchedModel ?? categoryDefault).Replace("Asobo", "")?.Trim();

            var test = installedAddons.FirstOrDefault(addon => addon.Title.Contains(aircraft.shortModel));

            if (installedAddons.FirstOrDefault(addon => addon.Title.StartsWith(matchedModel)) == null && installedAddons.FirstOrDefault(addon => addon.Title == matchedModel + " Asobo") == null)
            {
                Log.Information($"Failed to model match: {matchedModel} not installed!");
                if (installedAddons.FirstOrDefault(addon =>
                    addon.Title == (string)models.GetValue($"{matchedModel} Default")) != null)
                {
                    matchedModel = (string)models.GetValue($"{matchedModel} Default");
                }
                else
                {
                    matchedModel = installedAddons.FirstOrDefault(addon =>
                                       addon.ModelCode == aircraft.modelCode || TitleContains(addon, shortModel))
                                   ?.Title ??
                                   categoryDefault;
                }
            }

            if (TryFindAircraft(models, installedAddons, aircraft, matchedModel) != null)
            {
                matchedModel = TryFindAircraft(models, installedAddons, aircraft, matchedModel);
            }
            else
            {
                if (models.GetValue(matchedModel + " Default") == null) Log.Information($"Failed to model match: {matchedModel} Default");
                matchedModel = (string)models.GetValue($"{matchedModel} Default") ?? installedAddons.FirstOrDefault(addon => addon.ModelCode == aircraft.modelCode || addon.Title == matchedModel)?.Title ?? categoryDefault;
            }
            Log.Information($"Model matched model: {aircraft.modelCode}, airline: {aircraft.airline} with: {matchedModel}");
            return matchedModel;
        }

        private static string TryFindAircraft(JObject models, List<Addon> installedAddons, Aircraft aircraft, string matchedModel)
        {
            string foundAircraft = null;

            if (models.GetValue($"{matchedModel} {aircraft.airline}") != null)
            {
                foundAircraft = (string)models.GetValue($"{matchedModel} {aircraft.airline}");
            }
            else if (installedAddons.FirstOrDefault(addon => addon.Title.StartsWith($"{matchedModel} {aircraft.airline} AI")) != null)
            {
                foundAircraft = installedAddons.First(addon => addon.Title.StartsWith($"{matchedModel} {aircraft.airline} AI")).Title;
            }
            else if (installedAddons.FirstOrDefault(addon => addon.Title.Contains($"{matchedModel} {aircraft.airline}")) != null)
            {
                foundAircraft = installedAddons.First(addon => addon.Title.Contains($"{matchedModel} {aircraft.airline}")).Title;
            }
            else if (installedAddons.FirstOrDefault(addon => addon.Title.Contains(matchedModel) && addon.Icao_Airline == aircraft.icaoAirline) != null)
            {
                foundAircraft = installedAddons.First(addon => addon.Title.Contains(matchedModel) && addon.Icao_Airline == aircraft.icaoAirline).Title;
            }
            else if (installedAddons.FirstOrDefault(addon => (addon.Title.Contains(aircraft.modelCode) || addon.Title.Contains(matchedModel)) && (addon.Title.Contains(aircraft.icaoAirline) || addon.Title.Contains(aircraft.airline))) != null)
            {
                foundAircraft = installedAddons.First(addon => (addon.Title.Contains(aircraft.modelCode) || addon.Title.Contains(matchedModel)) && (addon.Title.Contains(aircraft.icaoAirline) || addon.Title.Contains(aircraft.airline))).Title;
            }
            else if (installedAddons.FirstOrDefault(addon => addon.ModelCode == aircraft.modelCode && (addon.Title.Contains(aircraft.icaoAirline) || addon.Title.Contains(aircraft.airline))) != null)
            {
                foundAircraft = installedAddons.First(addon => addon.ModelCode == aircraft.modelCode && (addon.Title.Contains(aircraft.icaoAirline) || addon.Title.Contains(aircraft.airline))).Title;
            }
            else if (installedAddons.FirstOrDefault(addon => (addon.ModelCode.Contains(aircraft.shorterModelCode) || addon.Title.Contains(aircraft.shorterModelCode) || addon.Title.Contains(aircraft.shortModel)) && (addon.Title.Contains(aircraft.icaoAirline) || addon.Title.Contains(aircraft.airline))) != null)
            {
                foundAircraft = installedAddons.First(addon => (addon.ModelCode.Contains(aircraft.shorterModelCode) || addon.Title.Contains(aircraft.shorterModelCode) || addon.Title.Contains(aircraft.shortModel)) && (addon.Title.Contains(aircraft.icaoAirline) || addon.Title.Contains(aircraft.airline))).Title;
            }

            return foundAircraft;
        }

        // Null/empty-safe Contains helpers. An empty needle would otherwise match every addon
        // (string.Contains("") == true) and pick an arbitrary aircraft.
        private static bool TitleContains(Addon addon, string value)
        {
            return !string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(addon.Title) && addon.Title.Contains(value);
        }

        private static bool CodeContains(Addon addon, string value)
        {
            return !string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(addon.ModelCode) && addon.ModelCode.Contains(value);
        }

        // Pick a sensible base aircraft by category so an unmapped type does not silently become
        // an A320 (e.g. a Cessna should fall back to a Cessna, a 777 to a heavy, etc.).
        private static string CategoryDefault(Aircraft aircraft)
        {
            string code = (aircraft.modelCode ?? string.Empty).ToUpperInvariant();
            string model = (aircraft.model ?? string.Empty).ToLowerInvariant();

            bool Has(params string[] keys) => keys.Any(k => model.Contains(k));
            bool CodeStarts(params string[] keys) => keys.Any(k => code.StartsWith(k));

            // Heavies / widebodies.
            if (CodeStarts("A30", "A31", "A33", "A34", "A35", "A38", "B74", "B76", "B77", "B78", "MD11", "IL96", "A124") ||
                Has("747", "767", "777", "787", "a330", "a340", "a350", "a380", "md-11", "ilyushin"))
                return "Boeing 747-8i";

            // Business jets.
            if (CodeStarts("C25", "C50", "C51", "C52", "C55", "C56", "C68", "C70", "C75", "CL30", "CL35", "CL60",
                    "E50", "E55", "FA", "GLF", "GL5", "GL6", "GL7", "LJ", "H25", "BE40", "PRM1") ||
                Has("citation", "learjet", "phenom", "challenger", "global", "falcon", "gulfstream", "hawker", "vision jet"))
                return "Cessna CJ4 Citation";

            // Turboprops (single and twin).
            if (CodeStarts("C208", "PC12", "TBM", "BE20", "B350", "B190", "DH8", "AT4", "AT7", "SF34", "SW4", "C212", "PC6") ||
                Has("caravan", "king air", "kingair", "tbm", "pilatus", "dash 8", "atr", "turboprop", "pc-12"))
                return "Cessna 208B Grand Caravan";

            // Light pistons / GA.
            if (CodeStarts("C15", "C17", "C18", "C20", "C21", "P28", "P32", "PA", "BE3", "BE5", "BE7", "DA4", "DA2",
                    "DR40", "DV20", "M20", "SR2", "RV", "TB1", "TB2", "AA5", "GLID", "C42", "P92") ||
                Has("cessna 1", "cessna 2", "cessna f1", "skyhawk", "skylane", "cirrus", "piper", "cherokee",
                    "warrior", "archer", "diamond", "mooney", "robin", "tecnam", "bonanza", "baron", "glider"))
                return "Cessna 172P Skyhawk G1000";

            // Default: narrowbody airliner.
            return "Airbus A320 Neo";
        }
    }
}
