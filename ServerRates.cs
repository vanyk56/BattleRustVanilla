using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("ServerRates", "Server", "1.0.0")]
    [Description("Единые рейты сервера: gather, pickup, quarry/excavator, животные, рыба, скрап и компоненты в ящиках")]
    public class ServerRates : RustPlugin
    {
        #region Config

        private class ConfigData
        {
            [JsonProperty("DefaultRate (дерево/камень/металл/сера/ткань и всё остальное, в т.ч. новые ресурсы)")]
            public float DefaultRate = 2.0f;

            [JsonProperty("PickupRate (подбираемые ресурсы: камни, руда, дерево, ткань, гриб и т.д.)")]
            public float PickupRate = 2.0f;

            [JsonProperty("QuarryRate")]
            public float QuarryRate = 2.0f;

            [JsonProperty("ExcavatorRate")]
            public float ExcavatorRate = 2.0f;

            [JsonProperty("AnimalRate (лут с туш животных)")]
            public float AnimalRate = 1.0f;

            [JsonProperty("FishRate")]
            public float FishRate = 1.0f;

            [JsonProperty("ItemOverrides (shortname -> рейт, приоритет над категориями)")]
            public Dictionary<string, float> ItemOverrides = new Dictionary<string, float>
            {
                { "sulfur.ore", 2.0f }
            };

            [JsonProperty("Loot")]
            public LootSettings Loot = new LootSettings();
        }

        private class LootSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("ScrapRate")]
            public float ScrapRate = 2.0f;

            [JsonProperty("ComponentRate")]
            public float ComponentRate = 2.0f;

            [JsonProperty("ScrapShortname")]
            public string ScrapShortname = "scrap";

            [JsonProperty("ComponentShortnames")]
            public List<string> ComponentShortnames = new List<string>
            {
                "gears", "metalpipe", "metalspring", "roadsigns", "sewingkit",
                "rope", "tarp", "sheetmetal", "semibody", "riflebody", "smgbody",
                "techparts", "propanetank", "metalblade", "fuse", "cctv.camera",
                "targeting.computer"
            };
        }

        private ConfigData cfg;
        private HashSet<string> componentSet;

        protected override void LoadDefaultConfig()
        {
            cfg = new ConfigData();
            PrintWarning("Создан новый конфиг по умолчанию");
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                cfg = Config.ReadObject<ConfigData>();
                if (cfg == null) throw new Exception("null config");
                if (cfg.ItemOverrides == null) cfg.ItemOverrides = new Dictionary<string, float>();
                if (cfg.Loot == null) cfg.Loot = new LootSettings();
                if (cfg.Loot.ComponentShortnames == null) cfg.Loot.ComponentShortnames = new List<string>();
            }
            catch
            {
                PrintError("Конфиг повреждён, загружены значения по умолчанию");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(cfg, true);

        #endregion

        #region Lifecycle

        // Оригинальные itemList подбираемых объектов (для восстановления при выгрузке плагина)
        private readonly Dictionary<CollectibleEntity, ItemAmount[]> originals =
            new Dictionary<CollectibleEntity, ItemAmount[]>();

        private void Init()
        {
            componentSet = new HashSet<string>(cfg.Loot.ComponentShortnames ?? new List<string>());
        }

        private void OnServerInitialized()
        {
            foreach (var net in BaseNetworkable.serverEntities)
            {
                var col = net as CollectibleEntity;
                if (col != null) ScaleCollectible(col);
            }
        }

        private void Unload()
        {
            foreach (var kv in originals)
            {
                if (kv.Key != null && !kv.Key.IsDestroyed)
                    kv.Key.itemList = kv.Value;
            }
            originals.Clear();
        }

        #endregion

        #region Helpers

        private static int Scale(int amount, float rate)
        {
            if (Mathf.Approximately(rate, 1f) || amount <= 0) return amount;
            float v = amount * rate;
            int result = Mathf.FloorToInt(v);
            // вероятностное округление, чтобы дробные рейты (x1.5) работали честно
            if (UnityEngine.Random.value < v - result) result++;
            return Mathf.Max(1, result);
        }

        private float RateFor(ItemDefinition def, float categoryRate)
        {
            float overrideRate;
            if (def != null && cfg.ItemOverrides.TryGetValue(def.shortname, out overrideRate))
                return overrideRate;
            return categoryRate;
        }

        private void ApplyRate(Item item, float categoryRate)
        {
            if (item == null || item.info == null) return;
            float rate = RateFor(item.info, categoryRate);
            item.amount = Scale(item.amount, rate);
        }

        #endregion

        #region Gather

        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            if (dispenser == null || item == null) return;
            float rate = dispenser.gatherType == ResourceDispenser.GatherType.Flesh
                ? cfg.AnimalRate
                : cfg.DefaultRate;
            ApplyRate(item, rate);
        }

        private void OnDispenserBonus(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            if (dispenser == null || item == null) return;
            float rate = dispenser.gatherType == ResourceDispenser.GatherType.Flesh
                ? cfg.AnimalRate
                : cfg.DefaultRate;
            ApplyRate(item, rate);
        }

        private void OnQuarryGather(MiningQuarry quarry, Item item)
        {
            ApplyRate(item, cfg.QuarryRate);
        }

        private void OnExcavatorGather(ExcavatorArm arm, Item item)
        {
            ApplyRate(item, cfg.ExcavatorRate);
        }

        private void OnFishCatch(Item fish, BaseFishingRod rod, BasePlayer player)
        {
            ApplyRate(fish, cfg.FishRate);
        }

        #endregion

        #region Pickup (CollectibleEntity)

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            var col = entity as CollectibleEntity;
            if (col != null) ScaleCollectible(col);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var col = entity as CollectibleEntity;
            if (col != null) originals.Remove(col);
        }

        private void ScaleCollectible(CollectibleEntity col)
        {
            if (col == null || col.itemList == null || originals.ContainsKey(col)) return;

            var source = col.itemList;
            // itemList общий для префаба — нельзя менять in-place, создаём копию для конкретного объекта
            var scaled = new ItemAmount[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                var ia = source[i];
                if (ia == null || ia.itemDef == null) { scaled[i] = ia; continue; }
                float rate = RateFor(ia.itemDef, cfg.PickupRate);
                float amount = Mathf.Max(1f, Mathf.Round(ia.amount * rate));
                scaled[i] = new ItemAmount(ia.itemDef, amount);
            }

            originals[col] = source;
            col.itemList = scaled;
        }

        #endregion

        #region Loot containers (ящики, бочки, и т.д.)

        private void OnLootSpawn(LootContainer container)
        {
            if (!cfg.Loot.Enabled || container == null) return;

            // лут появляется после хука — правим на следующем тике
            NextTick(() =>
            {
                if (container == null || container.IsDestroyed || container.inventory == null) return;
                foreach (var item in container.inventory.itemList.ToArray())
                {
                    if (item == null || item.info == null) continue;

                    string sn = item.info.shortname;
                    float rate;
                    if (sn == cfg.Loot.ScrapShortname) rate = cfg.Loot.ScrapRate;
                    else if (componentSet.Contains(sn)) rate = cfg.Loot.ComponentRate;
                    else continue;

                    int newAmount = Scale(item.amount, rate);
                    int max = item.MaxStackable();
                    item.amount = max > 0 ? Mathf.Min(newAmount, max) : newAmount;
                    item.MarkDirty();
                }
            });
        }

        #endregion
    }
}
