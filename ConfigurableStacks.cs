using System;
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("ConfigurableStacks", "Codex", "1.0.0")]
    [Description("Configurable item stack sizes")]
    public class ConfigurableStacks : RustPlugin
    {
        private Settings settings;
        private readonly Dictionary<ItemDefinition, int> originals = new Dictionary<ItemDefinition, int>();
        private readonly Dictionary<ItemDefinition, int> applied = new Dictionary<ItemDefinition, int>();

        private class Settings
        {
            public bool Enabled = true;
            public double GlobalMultiplier = 2;
            public int MaximumStackSize = 1000000;
            public bool AutoAddItems = true;
            public Dictionary<string, double> CategoryMultipliers = new Dictionary<string, double>();
            public Dictionary<string, int> ItemStackSizes = new Dictionary<string, int>();
            public List<string> ExcludedItems = new List<string> { "water", "water.salt" };
        }

        protected override void LoadDefaultConfig()
        {
            settings = new Settings();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                settings = Config.ReadObject<Settings>();
                if (settings == null) throw new Exception("Empty configuration.");
                if (!ValidMultiplier(settings.GlobalMultiplier))
                    throw new Exception("GlobalMultiplier must be finite and >= 1.");
                if (settings.MaximumStackSize < 1 || settings.MaximumStackSize > 1000000)
                    throw new Exception("MaximumStackSize must be 1..1000000.");
                if (settings.CategoryMultipliers == null || settings.ItemStackSizes == null || settings.ExcludedItems == null)
                    throw new Exception("Configuration collections cannot be null.");
                foreach (var entry in settings.CategoryMultipliers)
                    if (!ValidMultiplier(entry.Value)) throw new Exception("Invalid multiplier: " + entry.Key);
                foreach (var entry in settings.ItemStackSizes)
                    if (entry.Value < 0 || entry.Value > settings.MaximumStackSize)
                        throw new Exception("Invalid stack size: " + entry.Key);
            }
            catch (Exception ex)
            {
                settings = null;
                PrintError("Config was NOT overwritten. Fix it and reload: " + ex.Message);
            }
        }

        private static bool ValidMultiplier(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 1;
        }

        protected override void SaveConfig()
        {
            if (settings != null) Config.WriteObject(settings, true);
        }

        private void OnServerInitialized()
        {
            if (settings == null) return;
            var excluded = new HashSet<string>(settings.ExcludedItems);
            var names = new HashSet<string>();
            var categories = new HashSet<string>();
            bool configChanged = false;
            int changed = 0;
            foreach (var definition in ItemManager.itemList)
            {
                if (definition == null) continue;
                string name = definition.shortname;
                string category = definition.category.ToString();
                names.Add(name);
                categories.Add(category);
                if (settings.AutoAddItems && !settings.ItemStackSizes.ContainsKey(name))
                {
                    settings.ItemStackSizes[name] = 0;
                    configChanged = true;
                }
                if (!settings.Enabled || originals.ContainsKey(definition)) continue;
                // Preserve non-stackable items and items with durability.
                if (definition.stackable <= 1 ||
                    definition.condition.enabled ||
                    excluded.Contains(name)) continue;
                int target;
                if (!settings.ItemStackSizes.TryGetValue(name, out target) || target == 0)
                {
                    double multiplier;
                    if (!settings.CategoryMultipliers.TryGetValue(category, out multiplier))
                        multiplier = settings.GlobalMultiplier;
                    target = (int)Math.Min(settings.MaximumStackSize, Math.Floor(definition.stackable * multiplier));
                }
                target = Math.Max(1, Math.Min(target, settings.MaximumStackSize));
                if (target == definition.stackable) continue;
                originals[definition] = definition.stackable;
                applied[definition] = target;
                definition.stackable = target;
                changed++;
            }
            foreach (var entry in settings.ItemStackSizes)
                if (!names.Contains(entry.Key)) PrintWarning("Unknown item shortname: " + entry.Key);
            foreach (var category in settings.CategoryMultipliers.Keys)
                if (!categories.Contains(category)) PrintWarning("Unknown category: " + category);
            if (configChanged) SaveConfig();
            Puts("Changed stack limits for " + changed + " items.");
        }

        private void Unload()
        {
            foreach (var entry in originals)
            {
                int lastApplied;
                if (entry.Key != null && applied.TryGetValue(entry.Key, out lastApplied) && entry.Key.stackable == lastApplied)
                    entry.Key.stackable = entry.Value;
            }
            originals.Clear();
            applied.Clear();
        }
    }
}
