using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Oxide.Plugins
{
    [Info("SmeltingRate", "OpenAI", "1.1.0")]
    [Description("Scales native oven processing time and recycler intervals.")]
    public class SmeltingRate : RustPlugin
    {
        private const string AdminPermission = "smeltingrate.admin";
        private const BindingFlags Methods = BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static SmeltingRate instance;
        private Configuration settings;
        private Harmony harmony;
        private string patchId;
        private bool ready;
        private bool initialized;
        private MethodInfo recyclerStart;
        private MethodInfo recyclerStop;
        private readonly HashSet<MethodBase> patched = new HashSet<MethodBase>();
        private readonly Dictionary<BaseEntity, EntityState> entities =
            new Dictionary<BaseEntity, EntityState>();

        [ThreadStatic] private static WorkContext current;
        [ThreadStatic] private static HashSet<Item> suppressed;

        private enum Kind { Ignored, Furnace, LargeFurnace, ElectricFurnace, Refinery, OtherOven, Recycler }

        private sealed class EntityState
        {
            public Kind Kind;
            public MethodBase CookMethod;
            public Action<float> Cook;
            // Fractional part of accelerated ticks carried to the next tick,
            // so any speed (1.5, 2.5, 0.5...) stays exactly proportional.
            public float Carry;
        }

        private sealed class WorkContext : IDisposable
        {
            public readonly BaseEntity Entity;
            public Item Fuel;
            private readonly WorkContext previous;
            private List<Item> zeroItems;
            private bool disposed;

            public WorkContext(BaseEntity entity)
            {
                Entity = entity;
                previous = current;
                current = this;
            }

            public void Suppress(Item item)
            {
                if (suppressed == null) suppressed = new HashSet<Item>();
                if (!suppressed.Add(item)) return;
                if (zeroItems == null) zeroItems = new List<Item>();
                zeroItems.Add(item);
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                current = previous;
                if (zeroItems == null) return;
                foreach (Item item in zeroItems)
                    if (suppressed.Remove(item) && item.IsValid()) item.Remove();
            }
        }

        private sealed class Configuration
        {
            [JsonProperty("GlobalSmeltingSpeedMultiplier")]
            public float Speed = 2f;
            [JsonProperty("ApplyToFurnaces")]
            public bool Furnaces = true;
            [JsonProperty("ApplyToLargeFurnaces")]
            public bool LargeFurnaces = true;
            [JsonProperty("ApplyToElectricFurnaces")]
            public bool ElectricFurnaces = true;
            [JsonProperty("ApplyToRefineries")]
            public bool Refineries = true;
            [JsonProperty("ApplyToMonumentEntities")]
            public bool MonumentEntities = true;
            [JsonProperty("ApplyToDeployableEntities")]
            public bool DeployableEntities = true;
            [JsonProperty("ApplyToOtherOvens")]
            public bool OtherOvens = true;
            [JsonProperty("ApplyToRecyclers")]
            public bool Recyclers = true;
            [JsonProperty("OutputMultiplier")]
            public float Output = 1f;
            [JsonProperty("FuelConsumptionMultiplier")]
            public float Fuel = 1f;
            [JsonProperty("CharcoalMultiplier")]
            public float Charcoal = 1f;
            [JsonProperty("Debug")]
            public bool Debug = false;
        }

        protected override void LoadDefaultConfig()
        {
            settings = new Configuration();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            settings = Config.ReadObject<Configuration>();
            if (settings == null) throw new JsonException("SmeltingRate: empty configuration.");
            Validate(settings.Speed, 0.1f, "GlobalSmeltingSpeedMultiplier");
            Validate(settings.Output, 0f, "OutputMultiplier");
            Validate(settings.Fuel, 0.1f, "FuelConsumptionMultiplier");
            Validate(settings.Charcoal, 0f, "CharcoalMultiplier");
        }

        private static void Validate(float value, float minimum, string name)
        {
            if (!Finite(value) || value < minimum || value > 100f)
                throw new JsonException(name + " must be in [" + Number(minimum) + ", 100].");
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(settings, true);
        }

        private void Init()
        {
            instance = this;
            permission.RegisterPermission(AdminPermission, this);
        }

        private void OnServerInitialized()
        {
            try
            {
                InstallPatches();
                // One initial pass also covers entities loaded from the save.
                foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
                {
                    BaseEntity entity = networkable as BaseEntity;
                    if (entity is BaseOven || entity is Recycler) Track(entity);
                }
                ready = true;
                initialized = true;
                RestartRecyclers();
                SaveConfig();
                Puts("Loaded. Smelting speed: x" + Number(settings.Speed));
                if (settings.Debug)
                    Puts("Tracked entities: " + entities.Count + "; patched methods: " + patched.Count);
            }
            catch (Exception error)
            {
                ready = false;
                RemovePatches();
                PrintError("Initialization failed; acceleration disabled. " + error);
                NextTick(() => Interface.Oxide.UnloadPlugin(Name));
            }
        }

        private void OnEntitySpawned(BaseNetworkable networkable)
        {
            BaseEntity entity = networkable as BaseEntity;
            if (!(entity is BaseOven) && !(entity is Recycler)) return;
            NextTick(() =>
            {
                if (ready && entity != null && !entity.IsDestroyed) Track(entity);
            });
        }

        private void OnEntityKill(BaseNetworkable networkable)
        {
            BaseEntity entity = networkable as BaseEntity;
            if (!ReferenceEquals(entity, null)) entities.Remove(entity);
        }

        private void OnOvenCook(BaseOven oven, Item fuel)
        {
            if (current != null && ReferenceEquals(current.Entity, oven))
                current.Fuel = fuel;
        }

        private void Unload()
        {
            ready = false;
            RemovePatches();
            if (initialized) RestartRecyclers();
            entities.Clear();
            instance = null;
            Puts("Unloaded. Native processing restored.");
        }

        private EntityState Track(BaseEntity entity)
        {
            EntityState state;
            if (entities.TryGetValue(entity, out state)) return state;
            state = new EntityState { Kind = Classify(entity) };
            entities.Add(entity, state);
            if (settings.Debug)
                Puts("Detected " + entity.ShortPrefabName + " (" + entity.GetType().Name + "): " + state.Kind);
            return state;
        }

        private static bool InheritsNamed(Type type, string name)
        {
            while (type != null)
            {
                if (type.Name == name) return true;
                type = type.BaseType;
            }
            return false;
        }

        private static Kind Classify(BaseEntity entity)
        {
            if (entity is Recycler) return Kind.Recycler;
            if (!(entity is BaseOven)) return Kind.Ignored;
            // Lamps burn fuel but do not process resources.
            if (InheritsNamed(entity.GetType(), "BaseFuelLightSource")) return Kind.Ignored;
            string prefab = entity.ShortPrefabName ?? string.Empty;
            if (InheritsNamed(entity.GetType(), "ElectricOven")
                || prefab.IndexOf("electricfurnace", StringComparison.OrdinalIgnoreCase) >= 0)
                return Kind.ElectricFurnace;
            if (prefab.IndexOf("refinery", StringComparison.OrdinalIgnoreCase) >= 0)
                return Kind.Refinery;
            if (prefab.IndexOf("furnace.large", StringComparison.OrdinalIgnoreCase) >= 0)
                return Kind.LargeFurnace;
            if (prefab == "furnace" || prefab == "furnace.deployed")
                return Kind.Furnace;
            return Kind.OtherOven;
        }

        private bool Selected(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed) return false;
            // Standard map entities have OwnerID == 0. This also includes
            // unowned admin/plugin spawns. Player-owned objects use the other switch.
            if (entity.OwnerID == 0 ? !settings.MonumentEntities : !settings.DeployableEntities)
                return false;
            switch (Track(entity).Kind)
            {
                case Kind.Furnace: return settings.Furnaces;
                case Kind.LargeFurnace: return settings.LargeFurnaces;
                case Kind.ElectricFurnace: return settings.ElectricFurnaces;
                case Kind.Refinery: return settings.Refineries;
                case Kind.OtherOven: return settings.OtherOvens;
                case Kind.Recycler: return settings.Recyclers;
                default: return false;
            }
        }

        private void RestartRecyclers()
        {
            if (recyclerStart == null || recyclerStop == null) return;
            var snapshot = new List<BaseEntity>(entities.Keys);
            foreach (BaseEntity entity in snapshot)
            {
                Recycler recycler = entity as Recycler;
                if (recycler == null || recycler.IsDestroyed || !recycler.IsOn()) continue;
                if (!Selected(recycler)) continue;
                try
                {
                    recyclerStop.Invoke(recycler, null);
                    recyclerStart.Invoke(recycler, null);
                }
                catch (Exception error)
                {
                    PrintError("Could not restart recycler " + recycler.ShortPrefabName + ": " + error);
                }
            }
        }

        private static MethodInfo Method(string name)
        {
            return typeof(SmeltingRate).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        }

        private void Patch(MethodInfo target, string prefix = null, string transpiler = null,
            string finalizer = null)
        {
            patched.Add(target);
            harmony.Patch(target,
                prefix == null ? null : new HarmonyMethod(Method(prefix)),
                null,
                transpiler == null ? null : new HarmonyMethod(Method(transpiler)),
                finalizer == null ? null : new HarmonyMethod(Method(finalizer)));
        }

        private static MethodInfo Require(Type type, string name, params Type[] arguments)
        {
            MethodInfo method = AccessTools.Method(type, name, arguments);
            if (method == null || method.ReturnType != typeof(void) || method.IsStatic)
                throw new InvalidOperationException("Unsupported API: " + type.Name + "." + name);
            return method;
        }

        private void InstallPatches()
        {
            Require(typeof(BaseOven), "Cook", typeof(float));
            FieldInfo fuelField = AccessTools.Field(typeof(Item), "fuel");
            if (fuelField == null || fuelField.FieldType != typeof(float))
                throw new InvalidOperationException("Unsupported API: Item.fuel.");

            if (settings.Recyclers)
            {
                recyclerStart = Require(typeof(Recycler), "StartRecycling");
                recyclerStop = Require(typeof(Recycler), "StopRecycling");
                Require(typeof(Recycler), "RecycleThink");
            }

            patchId = "oxide.smeltingrate." + Guid.NewGuid().ToString("N");
            harmony = new Harmony(patchId);
            bool changeOutput = settings.Output != 1f || settings.Charcoal != 1f;
            bool changeFuel = settings.Fuel != 1f;
            int ovenMethods = 0, schedules = 0, outputSites = 0, fuelWrites = 0;
            foreach (Type type in typeof(BaseOven).Assembly.GetTypes())
            {
                bool oven = typeof(BaseOven).IsAssignableFrom(type);
                bool recycler = settings.Recyclers && typeof(Recycler).IsAssignableFrom(type);
                if (!oven && !recycler && !typeof(ItemModCookable).IsAssignableFrom(type)) continue;
                foreach (MethodInfo method in type.GetMethods(Methods))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null)
                        continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    bool cook = oven && !method.IsStatic && method.Name == "Cook"
                        && method.ReturnType == typeof(void) && parameters.Length == 1
                        && parameters[0].ParameterType == typeof(float);
                    bool recycle = changeOutput && recycler && !method.IsStatic && method.Name == "RecycleThink"
                        && method.ReturnType == typeof(void) && parameters.Length == 0;
                    bool transform = false;
                    foreach (CodeInstruction code in PatchProcessor.GetOriginalInstructions(method))
                    {
                        if (IsCreation(code))
                        {
                            outputSites++;
                            transform |= changeOutput;
                        }
                        if (code.opcode == OpCodes.Stfld && Equals(code.operand, fuelField))
                        {
                            fuelWrites++;
                            transform |= changeFuel;
                        }
                        if (recycler && !method.IsStatic && ScheduleArgument(code) >= 0)
                        {
                            schedules++;
                            transform = true;
                        }
                    }
                    if (cook) ovenMethods++;
                    if (cook || recycle || transform)
                        Patch(method, cook ? "CookPrefix" : recycle ? "RecyclerPrefix" : null,
                            transform ? "Transform" : null, recycle ? "RecyclerFinalizer" : null);
                }
            }
            if (ovenMethods == 0 || outputSites == 0 || fuelWrites == 0
                || (settings.Recyclers && schedules == 0))
                throw new InvalidOperationException(
                    "Native processing implementation changed. Cook=" + ovenMethods
                    + ", output sites=" + outputSites + ", fuel writes=" + fuelWrites
                    + ", recycler schedules=" + schedules + ". No partial acceleration is enabled.");

            // A fractional output may round down to zero. Let the native caller
            // see a successful placement without inserting a zero-sized item.
            if (settings.Output >= 1f && settings.Charcoal >= 1f) return;
            int moves = 0, drops = 0;
            foreach (MethodInfo method in typeof(Item).GetMethods(Methods))
            {
                if (method.IsStatic || method.GetMethodBody() == null) continue;
                if (method.Name == "MoveToContainer" && method.ReturnType == typeof(bool))
                {
                    Patch(method, "MovePrefix");
                    moves++;
                }
                else if (method.Name == "Drop")
                {
                    Patch(method, "DropPrefix");
                    drops++;
                }
            }
            if (moves == 0 || drops == 0)
                throw new InvalidOperationException("Unsupported Item placement API.");
        }

        private void RemovePatches()
        {
            if (harmony == null) return;
            foreach (MethodBase method in patched)
            {
                try { harmony.Unpatch(method, HarmonyPatchType.All, patchId); }
                catch (Exception error) { PrintError("Unpatch failed: " + method.Name + ": " + error); }
            }
            patched.Clear();
            harmony = null;
        }

        private static bool CookPrefix(BaseOven __instance, float __0, MethodBase __originalMethod)
        {
            SmeltingRate plugin = instance;
            if (plugin == null || !plugin.ready || !plugin.Selected(__instance)
                || !Finite(__0) || __0 <= 0f) return true;
            if (current != null && ReferenceEquals(current.Entity, __instance)) return true;

            EntityState state = plugin.Track(__instance);
            if (state.Cook == null || state.CookMethod != __originalMethod)
            {
                state.CookMethod = __originalMethod;
                state.Cook = (Action<float>)Delegate.CreateDelegate(
                    typeof(Action<float>), __instance, (MethodInfo)__originalMethod);
            }

            // Native Cook burns a fixed amount of fuel per call regardless of delta,
            // so scaling delta (old behaviour) breaks the fuel/ore/charcoal ratio for
            // any non-integer speed. Instead run whole, unmodified ticks: x2 = exactly
            // two native ticks, x1.5 = alternating 1 and 2 ticks, x0.5 = every other tick.
            state.Carry += plugin.settings.Speed;
            int steps = (int)state.Carry;
            state.Carry -= steps;
            if (steps <= 0) return false;
            using (new WorkContext(__instance))
            {
                for (int i = 0; i < steps; i++)
                {
                    if (__instance == null || __instance.IsDestroyed) break;
                    if (i > 0 && !__instance.IsOn()) break;
                    state.Cook(__0);
                }
            }
            return false;
        }

        private static void RecyclerPrefix(Recycler __instance, out WorkContext __state)
        {
            __state = null;
            SmeltingRate plugin = instance;
            if (plugin != null && plugin.ready && plugin.Selected(__instance)
                && (current == null || !ReferenceEquals(current.Entity, __instance)))
                __state = new WorkContext(__instance);
        }

        private static void RecyclerFinalizer(WorkContext __state)
        {
            if (__state != null) __state.Dispose();
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void WriteFuel(Item item, float next)
        {
            float old = item.fuel;
            SmeltingRate plugin = instance;
            if (plugin != null && plugin.ready && current != null
                && ReferenceEquals(current.Fuel, item)
                && current.Entity is BaseOven && next < old
                && plugin.Track(current.Entity).Kind != Kind.ElectricFurnace)
                next = old - (old - next) * plugin.settings.Fuel;
            item.fuel = next;
        }

        private static Item Produced(Item item)
        {
            SmeltingRate plugin = instance;
            if (item == null || item.amount <= 0 || current == null || plugin == null || !plugin.ready)
                return item;
            float multiplier = item.info.shortname == "charcoal"
                ? plugin.settings.Charcoal : plugin.settings.Output;
            if (multiplier == 1f) return item;
            double exact = (double)item.amount * multiplier;
            double whole = Math.Floor(exact);
            if (exact > whole && UnityEngine.Random.value < exact - whole) whole++;
            item.amount = (int)Math.Min(int.MaxValue, whole);
            if (item.amount == 0) current.Suppress(item);
            return item;
        }

        private static bool ConsumeSuppressed(Item item)
        {
            if (suppressed == null || !suppressed.Remove(item)) return false;
            if (item.IsValid()) item.Remove();
            return true;
        }

        private static bool MovePrefix(Item __instance, ref bool __result)
        {
            if (!ConsumeSuppressed(__instance)) return true;
            __result = true;
            return false;
        }

        private static bool DropPrefix(Item __instance)
        {
            return !ConsumeSuppressed(__instance);
        }

        private static float ScheduleTime(float value, BaseEntity entity, Delegate callback)
        {
            SmeltingRate plugin = instance;
            return plugin != null && plugin.ready && callback != null
                && callback.Method.Name == "RecycleThink" && plugin.Selected(entity)
                ? value / plugin.settings.Speed : value;
        }

        private static bool IsCreation(CodeInstruction code)
        {
            MethodInfo call = code.operand as MethodInfo;
            return (code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt)
                && call != null && call.IsStatic && call.DeclaringType == typeof(ItemManager)
                && call.ReturnType == typeof(Item) && call.Name.StartsWith("Create", StringComparison.Ordinal);
        }

        private static int ScheduleArgument(CodeInstruction code)
        {
            MethodInfo call = code.operand as MethodInfo;
            if ((code.opcode != OpCodes.Call && code.opcode != OpCodes.Callvirt) || call == null
                || (call.Name != "Invoke" && call.Name != "InvokeRepeating" && call.Name != "InvokeRandomized"))
                return -1;
            ParameterInfo[] parameters = call.GetParameters();
            int callback = -1;
            bool time = false;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (typeof(Delegate).IsAssignableFrom(parameters[i].ParameterType)) callback = i;
                if (parameters[i].ParameterType == typeof(float)) time = true;
                if (parameters[i].ParameterType.IsByRef) return -1;
            }
            return time ? callback : -1;
        }

        private static void MoveEntry(CodeInstruction source, CodeInstruction target)
        {
            target.labels.AddRange(source.labels);
            source.labels.Clear();
            for (int i = source.blocks.Count - 1; i >= 0; i--)
            {
                if (source.blocks[i].blockType == ExceptionBlockType.EndExceptionBlock) continue;
                target.blocks.Insert(0, source.blocks[i]);
                source.blocks.RemoveAt(i);
            }
        }

        private static IEnumerable<CodeInstruction> Transform(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator, MethodBase __originalMethod)
        {
            FieldInfo fuelField = AccessTools.Field(typeof(Item), "fuel");
            bool recycler = !__originalMethod.IsStatic
                && typeof(Recycler).IsAssignableFrom(__originalMethod.DeclaringType);
            foreach (CodeInstruction code in instructions)
            {
                if (instance.settings.Fuel != 1f && code.opcode == OpCodes.Stfld && Equals(code.operand, fuelField))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = Method("WriteFuel");
                    yield return code;
                    continue;
                }

                int callbackIndex = recycler ? ScheduleArgument(code) : -1;
                if (callbackIndex >= 0)
                {
                    MethodInfo call = (MethodInfo)code.operand;
                    ParameterInfo[] parameters = call.GetParameters();
                    var locals = new LocalBuilder[parameters.Length];
                    for (int i = 0; i < parameters.Length; i++)
                        locals[i] = generator.DeclareLocal(parameters[i].ParameterType);
                    bool first = true;
                    for (int i = parameters.Length - 1; i >= 0; i--)
                    {
                        var save = new CodeInstruction(OpCodes.Stloc, locals[i]);
                        if (first) { MoveEntry(code, save); first = false; }
                        yield return save;
                    }
                    LocalBuilder receiver = null;
                    if (!call.IsStatic)
                    {
                        receiver = generator.DeclareLocal(call.DeclaringType);
                        yield return new CodeInstruction(OpCodes.Stloc, receiver);
                        yield return new CodeInstruction(OpCodes.Ldloc, receiver);
                    }
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        yield return new CodeInstruction(OpCodes.Ldloc, locals[i]);
                        if (parameters[i].ParameterType == typeof(float))
                        {
                            yield return new CodeInstruction(OpCodes.Ldarg_0);
                            yield return new CodeInstruction(OpCodes.Ldloc, locals[callbackIndex]);
                            yield return new CodeInstruction(OpCodes.Call, Method("ScheduleTime"));
                        }
                    }
                    yield return code;
                    continue;
                }

                bool creation = (instance.settings.Output != 1f || instance.settings.Charcoal != 1f) && IsCreation(code);
                if (!creation)
                {
                    yield return code;
                    continue;
                }
                var adjust = new CodeInstruction(OpCodes.Call, Method("Produced"));
                for (int i = code.blocks.Count - 1; i >= 0; i--)
                {
                    if (code.blocks[i].blockType != ExceptionBlockType.EndExceptionBlock) continue;
                    adjust.blocks.Insert(0, code.blocks[i]);
                    code.blocks.RemoveAt(i);
                }
                yield return code;
                yield return adjust;
            }
        }

        private static string Number(float value)
        {
            return value.ToString("G", CultureInfo.InvariantCulture);
        }

        private bool Allowed(BasePlayer player)
        {
            return player != null && (player.IsAdmin
                || permission.UserHasPermission(player.UserIDString, AdminPermission));
        }

        private string Status()
        {
            return "SmeltingRate: скорость x" + Number(settings.Speed)
                + "; выход x" + Number(settings.Output)
                + "; расход топлива x" + Number(settings.Fuel)
                + "; уголь x" + Number(settings.Charcoal)
                + (ready ? "." : ". Обработка отключена: проверьте журнал сервера.");
        }

        private string SetSpeed(string text)
        {
            float value;
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || !Finite(value) || value < 0.1f || value > 100f)
                return "Укажите число от 0.1 до 100, например 2 или 1.5.";
            if (!ready) return "Плагин не активирован. Проверьте журнал сервера.";
            float previous = settings.Speed;
            settings.Speed = value;
            try { SaveConfig(); }
            catch (Exception error)
            {
                settings.Speed = previous;
                PrintError("Configuration save failed: " + error);
                return "Не удалось сохранить конфиг; скорость не изменена.";
            }
            RestartRecyclers();
            Puts("Smelting speed changed to x" + Number(value));
            return Status();
        }

        [ChatCommand("smeltingrate")]
        private void ChatRate(BasePlayer player, string command, string[] args)
        {
            if (!Allowed(player))
            {
                SendReply(player, "Нет права smeltingrate.admin.");
                return;
            }
            SendReply(player, args.Length == 0 ? Status()
                : args.Length == 1 ? SetSpeed(args[0]) : "Использование: /smeltingrate [0.1-100]");
        }

        [ConsoleCommand("smeltingrate.set")]
        private void ConsoleRate(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !Allowed(arg.Player()))
            {
                arg.ReplyWith("Нет права smeltingrate.admin.");
                return;
            }
            arg.ReplyWith(arg.Args != null && arg.Args.Length == 1
                ? SetSpeed(arg.Args[0].ToString()) : "Использование: smeltingrate.set <0.1-100>");
        }
    }
}
