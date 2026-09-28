using Facepunch;
using HarmonyLib;
using Network;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using ProtoBuf;
using Rust;
using Rust.Ai;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Vanish", "Whispers88", "2.1.5")]
    [Description("Allows players with permission to become invisible")]
    public class Vanish : CovalencePlugin
    {
        private static Vanish vanish;

        public static VanishedPlayers _hiddenPlayers;
        public class VanishedPlayers
        {
            public HashSet<ulong> _vanishedIDs = new HashSet<ulong>();
            public HashSet<BasePlayer> _vanishedPlayers = new HashSet<BasePlayer>();

            public int Count => _vanishedIDs.Count;

            public void Add(BasePlayer player)
            {
                if (player == null) return;
                _vanishedIDs.Add(player.userID);
                _vanishedPlayers.Add(player);
            }
            public void Remove(BasePlayer player)
            {
                if (player == null) return;
                _vanishedIDs.Remove(player.userID);
                _vanishedPlayers.Remove(player);
            }

            public bool IsHidden(ulong playerID)
            {
                return _vanishedIDs.Contains(playerID);
            }
        }

        private List<ulong> _hiddenOffline = new List<ulong>();
        private List<string> _registeredhooks = new List<string> { "CanUseLockedEntity", "OnEntityTakeDamage", "OnPlayerViolation", "OnMapMarkerAdd" };
        private int PlayerLayermask = LayerMask.GetMask(LayerMask.LayerToName((int)Layer.Player_Server));
        private DamageTypeList _EmptyDmgList = new DamageTypeList();

        CuiElementContainer cachedVanishUI = null;

        #region Configuration

        private Configuration config;

        public class Configuration
        {
            [JsonProperty("NoClip on Vanish (runs noclip command)")]
            public bool NoClipOnVanish = true;

            [JsonProperty("Inventory view cmd", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public string[] InvViewCMD = new[] { "inv", "invspy" };

            [JsonProperty("Use OnEntityTakeDamage hook (Set to true to enable use of vanish.damage perm. Set to false for better performance)")]
            public bool UseOnEntityTakeDamage = false;

            [JsonProperty("Use CanUseLockedEntity hook (Allows vanished players with the perm vanish.unlock to bypass locks. Set to false for better performance)")]
            public bool UseCanUseLockedEntity = true;

            [JsonProperty("Automatically vanish players (with the vanish.use perm) on player connect")]
            public bool EnforceOnConnect = false;

            [JsonProperty("Automatically vanish players (with the vanish.use perm) on player disconnect")]
            public bool EnforceOnDisconnect = false;

            [JsonProperty("Keep a vanished player hidden on disconnect")]
            public bool HideOnDisconnect = true;

            [JsonProperty("Teleport a vanished player under the map on disconnect")]
            public bool UnderWorldOnDisconnect = false;

            [JsonProperty("Teleport a vanished player above the map on connect")]
            public bool AboveWorldOnConnect = true;

            [JsonProperty("Bypass violation checks for vanished players")]
            public bool BypassViolation = true;

            [JsonProperty("Turn off fly hack detection for players in vanish")]
            public bool AntiHack = true;

            [JsonProperty("Disable metabolism in vanish")]
            public bool Metabolism = true;

            [JsonProperty("Enable teleport to marker when vanished")]
            public bool TeleportToMarker = false;

            [JsonProperty("Enable vanishing and reappearing sound effects")]
            public bool EnableSound = true;

            [JsonProperty("Make sound effects public")]
            public bool PublicSound = false;

            [JsonProperty("Enable chat notifications")]
            public bool EnableNotifications = true;

            [JsonProperty("Sound effect to use when vanishing")]
            public string VanishSoundEffect = "assets/prefabs/npc/patrol helicopter/effects/rocket_fire.prefab";

            [JsonProperty("Sound effect to use when reappearing")]
            public string ReappearSoundEffect = "assets/prefabs/npc/patrol helicopter/effects/rocket_fire.prefab";

            [JsonProperty("Enable GUI")]
            public bool EnableGUI = true;

            [JsonProperty("Use Native Invis Icon")]
            public bool NativeIcon = false;

            [JsonProperty("Icon URL (.png or .jpg)")]
            public string ImageUrlIcon = "https://i.ibb.co/3rZzftx/yL9HNRy.png";

            [JsonProperty("Icon Sprite")]
            public string ImageSprite = "assets/icons/refresh.png";

            [JsonProperty("Image Color")]
            public string ImageColor = "1 1 1 0.5";

            [JsonProperty("Image AnchorMin")]
            public string ImageAnchorMin = "0.175 0.017";

            [JsonProperty("Image AnchorMax")]
            public string ImageAnchorMax = "0.22 0.08";

            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonConvert.DeserializeObject<Dictionary<string, object>>(ToJson());
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null)
                {
                    throw new JsonException();
                }

                if (!config.ToDictionary().Keys.SequenceEqual(Config.ToDictionary(x => x.Key, x => x.Value).Keys))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch
            {
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            LogWarning($"Configuration changes saved to {Name}.json");
            Config.WriteObject(config, true);
        }

        private void Loaded()
        {
            _hiddenOfflineData = Interface.Oxide.DataFileSystem.GetFile("VanishPlayers");
            LoadData();
            InitVanishedPlayers();
        }
        private void InitVanishedPlayers()
        {
            foreach (var playerid in _hiddenOffline)
            {
                BasePlayer player = BasePlayer.FindByID(playerid);
                if (player == null) continue;

                if (IsInvisible(player))
                    continue;

                if (!player.IsConnected)
                {
                    List<Connection> connections = Pool.Get<List<Connection>>();
                    foreach (var con in Net.sv.connections)
                    {
                        if (con.connected && con.isAuthenticated && con.player is BasePlayer && con.player != player)
                            connections.Add(con);
                    }
                    player.OnNetworkSubscribersLeave(connections);
                    player.DisablePlayerCollider();
                    player.syncPosition = false;
                    player.limitNetworking = true;
                    Pool.FreeUnmanaged(ref connections);
                }
                else
                {
                    Disappear(player);
                }
            }
        }
        #endregion Configuration

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["VanishCommand"] = "vanish",
                ["Vanished"] = "Vanish: <color=orange> Enabled </color>",
                ["Reappear"] = "Vanish: <color=orange> Disabled </color>",
                ["NoPerms"] = "You do not have permission to do this",
                ["PermanentVanish"] = "You are in a permanent vanish mode",
                ["NoPlayers"] = "No players found using id: {0}"

            }, this);
        }

        #endregion Localization

        #region Initialization

        private const string PermAllow = "vanish.allow";
        private const string PermUnlock = "vanish.unlock";
        private const string PermDamage = "vanish.damage";
        private const string PermVanish = "vanish.permanent";
        private const string PermInvView = "vanish.invviewer";
        private const string PermTeleport = "vanish.teleport";


        private void Init()
        {
            vanish = this;
            _hiddenPlayers = new VanishedPlayers();
            cachedVanishUI = CreateVanishUI();

            // Register universal chat/console commands
            AddLocalizedCommand(nameof(VanishCommand));
            AddCovalenceCommand(config.InvViewCMD, "InventoryViewerCMD");
            AddCovalenceCommand(new[] { "debug.invis", "invis" }, "RedirectCMD");

            // Register permissions for commands
            permission.RegisterPermission(PermAllow, this);
            permission.RegisterPermission(PermUnlock, this);
            permission.RegisterPermission(PermDamage, this);
            permission.RegisterPermission(PermVanish, this);
            permission.RegisterPermission(PermInvView, this);
            permission.RegisterPermission(PermTeleport, this);

            //Unsubscribe from hooks
            UnSubscribeFromHooks();

            if (!config.UseOnEntityTakeDamage)
            {
                _registeredhooks.Remove("OnEntityTakeDamage");
            }

            if (!config.BypassViolation)
            {
                _registeredhooks.Remove("OnPlayerViolation");
            }

            if (!config.UseCanUseLockedEntity)
            {
                _registeredhooks.Remove("CanUseLockedEntity");
            }

            if (!config.TeleportToMarker)
            {
                _registeredhooks.Remove("OnMapMarkerAdd");
            }

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (!HasPerm(player.UserIDString, PermVanish) || IsInvisible(player)) continue;
                Disappear(player);
            }

        }

        private void Unload()
        {
            foreach (var hiddenPlayer in _hiddenPlayers._vanishedPlayers.ToList())
            {
                if (hiddenPlayer == null) continue;

                if (!_hiddenOffline.Contains(hiddenPlayer.userID))
                    _hiddenOffline.Add(hiddenPlayer.userID);
                Reappear(hiddenPlayer);
            }

            SaveData();
            vanish = null;
            _registeredhooks = null;
            _EmptyDmgList = null;
        }

        private DynamicConfigFile _hiddenOfflineData;

        private void LoadData()
        {
            try
            {
                _hiddenOffline = _hiddenOfflineData.ReadObject<List<ulong>>();
            }
            catch
            {
                _hiddenOffline = new List<ulong>();
            }
            Puts("Load Data");
        }

        private void SaveData()
        {
            _hiddenOfflineData.WriteObject(_hiddenOffline);
        }

        #endregion Initialization

        #region Commands

        private void RedirectCMD(IPlayer iplayer, string command, string[] args)
        {
            BasePlayer basePlayer = iplayer.Object as BasePlayer;
            if (basePlayer == null) return;
            basePlayer.ConsoleMessage("This command has been replaced with /vanish");
            VanishCommand(iplayer, command, args);
        }

        private void InventoryViewerCMD(IPlayer iplayer, string command, string[] args)
        {
            BasePlayer player = (BasePlayer)iplayer.Object;
            if (player == null) return;
            if (!HasPerm(player.UserIDString, PermInvView))
            {
                if (config.EnableNotifications) Message(player.IPlayer, "NoPerms");
                return;
            }
            if (args.Length < 1)
            {
                RaycastHit raycastHit;
                if (!Physics.Raycast(player.eyes.HeadRay(), out raycastHit, 5f, PlayerLayermask))
                    return;

                BasePlayer? entity = raycastHit.GetEntity() as BasePlayer;

                if (entity == null)
                {
                    Message(player.IPlayer, "NoPlayers", "NoArgs");
                    return;
                }

                timer.Once(0.3f, () => { StartLootingPlayer(player, entity); });
                return;

            }
            BasePlayer? foundplayer = null;
            if (ulong.TryParse(args[0], out ulong steamID))
            {
                foundplayer = BasePlayer.FindAwakeOrSleepingByID(steamID);
            }
            else
            {
                foreach (var p in BasePlayer.allPlayerList)
                {
                    if (!p.displayName.StartsWith(args[0], StringComparison.CurrentCultureIgnoreCase))
                        continue;

                    foundplayer = p;
                    break;
                }
            }
            if (foundplayer == null)
            {
                Message(player.IPlayer, "NoPlayers", args[0]);
                return;
            }

            timer.Once(0.3f, () => { StartLootingPlayer(player, foundplayer); });
        }

        private void StartLootingPlayer(BasePlayer player, BasePlayer foundplayer)
        {
            if (player == null || foundplayer == null)
                return;
            player.inventory.loot.AddContainer(foundplayer.inventory.containerMain);
            player.inventory.loot.AddContainer(foundplayer.inventory.containerWear);
            player.inventory.loot.AddContainer(foundplayer.inventory.containerBelt);
            player.inventory.loot.entitySource = RelationshipManager.ServerInstance;
            player.inventory.loot.PositionChecks = false;
            player.inventory.loot.MarkDirty();
            player.inventory.loot.SendImmediate();
            player.ClientRPC(RpcTarget.Player("RPC_OpenLootPanel", player), "player_corpse");
        }

        private void VanishCommand(IPlayer iplayer, string command, string[] args)
        {
            BasePlayer player = (BasePlayer)iplayer.Object;
            if (player == null) return;
            if (!HasPerm(player.UserIDString, PermAllow))
            {
                if (config.EnableNotifications) Message(player.IPlayer, "NoPerms");
                return;
            }
            if (HasPerm(player.UserIDString, PermVanish))
            {
                if (config.EnableNotifications) Message(player.IPlayer, "PermanentVanish");
                return;
            }

            //allows you to keybind vanish command with true/false args to force vanish or reappear
            if (args.Length > 0 && args[0] != "True" && bool.TryParse(args[0], out bool wantsVanish))
            {
                if (wantsVanish && !IsInvisible(player))
                {
                    Disappear(player);
                }
                else if (!wantsVanish && IsInvisible(player))
                {
                    Reappear(player);
                }
                return;
            }

            if (IsInvisible(player)) Reappear(player);
            else Disappear(player);
        }

        private string drowneffect = "28ad47c8e6d313742a7a2740674a25b5";
        private string falldamageeffect = "ca14ed027d5924003b1c5d9e523a5fce";
        private void Reappear(BasePlayer player)
        {
            if (Interface.CallHook("OnVanishReappear", player) != null) return;


            if (config.AntiHack) BasePlayer.ResetAntiHack(player, AntiHack.PlayerStates, AntiHack.PlayerNoclipStates, AntiHack.PlayerSpeedhackStates, AntiHack.PlayerFlyhackStates);

            player.syncPosition = true;

            VanishPositionUpdate vanishPositionUpdate;
            if (player.TryGetComponent<VanishPositionUpdate>(out vanishPositionUpdate))
                UnityEngine.Object.Destroy(vanishPositionUpdate);

            SimpleAIMemory.RemoveIgnorePlayer(player);
            BaseEntity.Query.Server.RemovePlayer(player); // have to remove first in case of other plugins
            BaseEntity.Query.Server.AddPlayer(player);

            player._limitedNetworking = false;
            player.isInvisible = false; // for occlusion falldmg & antihack
            BasePlayer.invisPlayers.Clear(); //im sure this will break at some point but for now its the fix for double voices

            _hiddenPlayers.Remove(player);

            player.EnablePlayerCollider();
            player.UpdateNetworkGroup();
            player.SendNetworkUpdate();
            player.GetHeldEntity()?.SendNetworkUpdate();

            //Un-Mute Player Effects
            player.drownEffect.guid = drowneffect;
            player.fallDamageEffect.guid = falldamageeffect;

            if (config.Metabolism)
            {
                RestartMetabolism(player);
            }

            if (_hiddenPlayers.Count == 0) UnSubscribeFromHooks();

            if (config.EnableSound)
            {
                if (config.PublicSound)
                {
                    Effect.server.Run(config.ReappearSoundEffect, player.transform.position);
                }
                else
                {
                    SendEffect(player, config.ReappearSoundEffect);
                }
            }

            CuiHelper.DestroyUi(player, "VanishUI");

            if (config.NativeIcon)
            {
                player.SendConsoleCommand("debug.setinvis_ui false");
            }

            if (config.NoClipOnVanish && player.IsFlying) player.SendConsoleCommand(noclip);

            if (config.EnableNotifications) Message(player.IPlayer, "Reappear");
        }

        private GameObjectRef _emptygameObject = new GameObjectRef();
        private const string noclip = "noclip";

        private void Disappear(BasePlayer player)
        {
            _hiddenPlayers.Add(player);

            if (Interface.CallHook("OnVanishDisappear", player) != null) return;

            if (config.AntiHack)
            {
                player.PauseFlyHackDetection(float.MaxValue);
            }

            SimpleAIMemory.AddIgnorePlayer(player);
            BaseEntity.Query.Server.RemovePlayer(player);

            player.syncPosition = false;
            player.limitNetworking = true;
            player.isInvisible = true; // for occlusion falldmg & antihack
            BasePlayer.invisPlayers.Clear(); //im sure this will break at some point but for now its the fix for double voices
            player.fallDamageEffect = _emptygameObject;
            player.drownEffect = _emptygameObject;
            player.GetHeldEntity()?.SetHeld(false);
            player.DisablePlayerCollider();

            if (config.Metabolism)
            {
                MetabolismPause(player);
            }

            List<Connection> connections = Pool.Get<List<Connection>>();
            foreach (var con in Net.sv.connections)
            {
                if (con.connected && con.isAuthenticated && con.player is BasePlayer && con.player != player)
                    connections.Add(con);
            }

            player.OnNetworkSubscribersLeave(connections);
            Pool.FreeUnmanaged(ref connections);

            if (ServerOcclusion.OcclusionEnabled)
            {
                player.OcclusionMakeSubscribersForget();
            }

            VanishPositionUpdate vanishPositionUpdate;
            if (player.TryGetComponent<VanishPositionUpdate>(out vanishPositionUpdate))
                UnityEngine.Object.Destroy(vanishPositionUpdate);

            player.gameObject.AddComponent<VanishPositionUpdate>();

            if (_hiddenPlayers.Count == 1) SubscribeToHooks();

            if (config.EnableSound)
            {
                if (config.PublicSound)
                {
                    Effect.server.Run(config.VanishSoundEffect, player.transform.position);
                }
                else
                {
                    SendEffect(player, config.VanishSoundEffect);
                }
            }

            if (config.NoClipOnVanish && !player.IsFlying && !player.isMounted)
                player.SendConsoleCommand(noclip);

            if (config.EnableGUI)
            {
                CuiHelper.AddUi(player, cachedVanishUI);
            }

            if (config.NativeIcon)
            {
                player.SendConsoleCommand("debug.setinvis_ui true");
            }
            else
            {
                player.SendConsoleCommand("debug.setinvis_ui false"); //for first login
            }

            if (config.EnableNotifications) Message(player.IPlayer, "Vanished");
        }

        private void MetabolismPause(BasePlayer player)
        {
            if (player == null) return;

            player.metabolism.calories.value = 500;
            player.metabolism.hydration.value = 250;
            player.metabolism.temperature.min = 20;
            player.metabolism.temperature.max = 20;
            player.metabolism.temperature.value = 20;
            player.metabolism.radiation_poison.value = 0;
            player.metabolism.radiation_poison.max = 0;
            player.metabolism.oxygen.value = 1;
            player.metabolism.oxygen.min = 1;
            player.metabolism.wetness.max = 0;
            player.metabolism.wetness.value = 0;
            player.metabolism.calories.min = player.metabolism.calories.value;

            player.SetHealth(player.MaxHealth());
            player.metabolism.SendChanges();
            player.metabolism.timeSinceLastMetabolism = 0f;

        }

        private void RestartMetabolism(BasePlayer player)
        {
            if (player == null) return;
            player.metabolism.timeSinceLastMetabolism = 1f;
            player.metabolism.temperature.min = -100;
            player.metabolism.temperature.max = 100;
            player.metabolism.radiation_poison.max = 500;
            player.metabolism.oxygen.min = 0;
            player.metabolism.calories.min = 0;
            player.metabolism.wetness.max = 1;
            player.SendNetworkUpdate();
        }

        #endregion Commands

        #region Hooks
        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;

            if (player.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot))
            {
                timer.Once(3f, () => OnPlayerConnected(player));
                return;
            }
            if (config.AboveWorldOnConnect && player._limitedNetworking)
            {
                float terrainY = TerrainMeta.HeightMap.GetHeight(player.transform.position);
                if (player.transform.position.y < terrainY)
                    player.transform.position = new Vector3(player.transform.position.x, terrainY + 0.5f, player.transform.position.z);
            }
            if (_hiddenOffline.Contains(player.userID))
            {
                _hiddenOffline.Remove(player.userID);
                if (HasPerm(player.UserIDString, PermAllow))
                {
                    Disappear(player);
                    return;
                }
                else Reappear(player);
            }

            if (HasPerm(player.UserIDString, PermVanish))
            {
                Disappear(player);
                return;
            }

            if (config.EnforceOnConnect && HasPerm(player.UserIDString, PermAllow))
            {
                Disappear(player);
                return;
            }
        }

        private object CanUseLockedEntity(BasePlayer player, BaseLock baseLock)
        {
            if (!player.limitNetworking) return null;
            if (HasPerm(player.UserIDString, PermUnlock)) return true;
            if (config.EnableNotifications) Message(player.IPlayer, "NoPerms");
            return null;
        }

        private object? OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (info == null || entity == null)
                return null;

            BasePlayer attacker = info.InitiatorPlayer;
            BasePlayer victim = entity.ToPlayer();
            if (!IsInvisible(victim) && !IsInvisible(attacker)) return null;
            if (attacker == null) return null;
            if (IsInvisible(attacker) && HasPerm(attacker.UserIDString, PermDamage)) return null;
            info.damageTypes = _EmptyDmgList;
            info.HitMaterial = 0;
            info.PointStart = Vector3.zero;
            info.DoHitEffects = false;
            info.HitEntity = null;
            return true;
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (config.EnforceOnDisconnect && HasPerm(player.UserIDString, PermAllow))
            {
                Disappear(player);
            }

            if (!IsInvisible(player)) return;

            if (!HasPerm(player.UserIDString, PermVanish) && (!HasPerm(player.UserIDString, PermAllow) && config.HideOnDisconnect))
                Reappear(player);
            else
            {
                if (config.UnderWorldOnDisconnect)
                {
                    float terrainY = TerrainMeta.HeightMap.GetHeight(player.transform.position);
                    if (player.transform.position.y > terrainY)
                        player.transform.position = new Vector3(player.transform.position.x, terrainY - 5f, player.transform.position.z);
                }

                if (!_hiddenOffline.Contains(player.userID))
                    _hiddenOffline.Add(player.userID);

                _hiddenPlayers.Remove(player);
                VanishPositionUpdate t;
                if (player.TryGetComponent<VanishPositionUpdate>(out t))
                    UnityEngine.Object.Destroy(t);
            }
            if (_hiddenPlayers.Count == 0) UnSubscribeFromHooks();
        }

        private void OnPlayerSpectate(BasePlayer player, string spectateFilter)
        {
            VanishPositionUpdate vanishPositionUpdate;
            if (!player.TryGetComponent<VanishPositionUpdate>(out vanishPositionUpdate)) return;
            UnityEngine.Object.Destroy(vanishPositionUpdate);
        }

        private void OnPlayerSpectateEnd(BasePlayer player, string spectateFilter)
        {
            if (!player._limitedNetworking) return;

            VanishPositionUpdate vanishPositionUpdate;
            if (!player.TryGetComponent<VanishPositionUpdate>(out vanishPositionUpdate))
                player.gameObject.AddComponent<VanishPositionUpdate>().EndSpectate();
        }

        private object? OnPlayerColliderEnable(BasePlayer player, CapsuleCollider collider) => IsInvisible(player) ? (object)true : null;

        private object? OnPlayerViolation(BasePlayer player, AntiHackType type, float amount) => IsInvisible(player) ? (object)true : null;

        private object OnMapMarkerAdd(BasePlayer player, MapNote note)
        {
            if (player.isMounted || !IsInvisible(player) || !HasPerm(player.UserIDString, PermTeleport) || !player.serverInput.IsDown(BUTTON.RELOAD))
                return null;

            player.serverInput.Clear();

            UnityEngine.Vector3 newpos = new UnityEngine.Vector3(note.worldPosition.x, player.transform.position.y, note.worldPosition.z);
            player.Teleport(newpos);
            note.Dispose();
            return true;
        }

        #endregion Hooks

        #region GUI
        private CuiElementContainer CreateVanishUI()
        {
            CuiElementContainer elements = new CuiElementContainer();
            string panel = elements.Add(new CuiPanel
            {
                Image = { Color = "0.5 0.5 0.5 0.0" },
                RectTransform = { AnchorMin = config.ImageAnchorMin, AnchorMax = config.ImageAnchorMax }
            }, "Hud.Menu", "VanishUI");
            elements.Add(new CuiElement
            {
                Parent = panel,
                Components =
                {
                    new CuiRawImageComponent {Color = config.ImageColor, Url = config.ImageUrlIcon, Sprite = config.ImageSprite},
                    new CuiRectTransformComponent {AnchorMin = "0 0", AnchorMax = "1 1"}
                }
            });
            return elements;
        }

        #endregion GUI

        #region Monobehaviour
        public class VanishPositionUpdate : FacepunchBehaviour
        {
            private BasePlayer player;
            private static int Layermask = LayerMask.GetMask(LayerMask.LayerToName((int)Layer.Construction), LayerMask.LayerToName((int)Layer.Deployed), LayerMask.LayerToName((int)Layer.Vehicle_World), LayerMask.LayerToName((int)Layer.Player_Server));
            LootableCorpse corpse;
            GameObject child;
            SphereCollider col;
            int LayerReserved1 = (int)Layer.Reserved1;
            BUTTON _reload = BUTTON.RELOAD;

            private void Awake()
            {
                player = GetComponent<BasePlayer>();
                player.transform.localScale = Vector3.zero;
                CreateChildGO();
            }

            private void FixedUpdate()
            {
                if (player == null)
                    return;

                player.metabolism.timeSinceLastMetabolism = 0f; // pause metabolism

                if (!player.serverInput.IsDown(_reload) || !player.serverInput.WasDown(_reload))
                    return;

                player.serverInput.previous.buttons = 0;

                RaycastHit raycastHit;
                if (!Physics.Raycast(player.eyes.HeadRay(), out raycastHit, 5f, Layermask))
                    return;

                BaseEntity entity = raycastHit.GetEntity() as BaseEntity;

                if (entity == null) return;

                if (entity is StorageContainer container)
                {
                    player.inventory.loot.Clear();
                    player.inventory.loot.AddContainer(container.inventory);
                    player.inventory.loot.entitySource = RelationshipManager.ServerInstance;
                    player.inventory.loot.PositionChecks = false;
                    player.inventory.loot.MarkDirty();
                    player.SendNetworkUpdateImmediate();
                    player.ClientRPC(RpcTarget.Player("RPC_OpenLootPanel", player), "generic_resizable");
                    return;
                }

                if (entity is BasePlayer targetplayer)
                {
                    if (!vanish.HasPerm(player.UserIDString, PermInvView))
                        return;

                    player.inventory.loot.AddContainer(targetplayer.inventory.containerMain);
                    player.inventory.loot.AddContainer(targetplayer.inventory.containerWear);
                    player.inventory.loot.AddContainer(targetplayer.inventory.containerBelt);
                    player.inventory.loot.entitySource = RelationshipManager.ServerInstance;
                    player.inventory.loot.PositionChecks = false;
                    player.inventory.loot.MarkDirty();
                    player.inventory.loot.SendImmediate();
                    player.ClientRPC(RpcTarget.Player("RPC_OpenLootPanel", player), "player_corpse");
                    return;
                }

                if (entity is Door door)
                {
                    if (door.IsOpen())
                    {
                        door.SetOpen(false, true);
                    }
                    else
                    {
                        door.SetOpen(true, false);
                    }
                    return;
                }

                BaseMountable component = entity.GetComponent<BaseMountable>();
                if (component == null)
                    return;
                component.AttemptMount(player, true);

            }

            private void UpdatePos()
            {
                if (player == null)
                    return;

                player.net.UpdateGroups(player.transform.position, player.networkRange);
            }

            void OnTriggerEnter(Collider col)
            {
                TriggerParent triggerParent = col.GetComponentInParent<TriggerParent>();
                if (triggerParent != null)
                {
                    triggerParent.OnTriggerEnter(player.playerCollider);
                    return;
                }

                DeepSeaManager deepSeaManager = PointEntity<DeepSeaManager>.ServerInstance;
                if (deepSeaManager != null)
                {
                    var trig = col.GetComponentInParent<TriggerDeepSeaPortal>();
                    if (trig != null)
                    {
                        if (trig.Portal.PortalMode == DeepSeaPortal.PortalModeEnum.Entrance)
                        {
                            deepSeaManager.MoveToDeepSea(player);
                        }
                        else
                        {
                            deepSeaManager.MoveToMainIsland(player);
                        }
                        return;
                    }
                }

                TriggerWorkbench triggerWorkbench = col.GetComponentInParent<TriggerWorkbench>();
                if (triggerWorkbench == null)
                    return;

                player.EnterTrigger(triggerWorkbench);
                player.nextCheckTime = float.MaxValue;
                player.cachedCraftLevel = triggerWorkbench.parentBench.Workbenchlevel;

                switch (player.cachedCraftLevel)
                {
                    case 1:
                        player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench1, true); break;
                    case 2:
                        player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench2, true); break;
                    case 3:
                        player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench3, true); break;
                }
            }

            void OnTriggerExit(Collider col)
            {
                TriggerParent triggerParent = col.GetComponentInParent<TriggerParent>();
                if (triggerParent != null)
                {
                    triggerParent.OnTriggerExit(player.playerCollider);
                    return;
                }
                TriggerWorkbench triggerWorkbench = col.GetComponentInParent<TriggerWorkbench>();
                if (triggerWorkbench != null)
                {
                    player.LeaveTrigger(triggerWorkbench);
                    player.cachedCraftLevel = 0f;
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench1, false);
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench2, false);
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench3, false);
                    player.nextCheckTime = Time.realtimeSinceStartup;
                    return;
                }
            }

            public void EndSpectate()
            {
                InvokeRepeating(RespawnCheck, 1f, 0.5f);
            }

            public void RespawnCheck()
            {
                if (player == null || !player.IsAlive()) return;
                CancelInvoke(RespawnCheck);
                player.SetPlayerFlag(BasePlayer.PlayerFlags.Spectating, false);
                player.SendNetworkUpdateImmediate();
                CreateChildGO();
            }

            public void CreateChildGO()
            {
                if (player == null || player.IsSpectating())
                    return;

                player.transform.localScale = Vector3.zero;
                child = gameObject.CreateChild();
                col = child.AddComponent<SphereCollider>();
                child.layer = LayerReserved1;
                child.transform.localScale = Vector3.zero;
                col.isTrigger = true;

                InvokeRepeating("UpdatePos", 1f, 5f);
            }

            private void OnDestroy()
            {
                CancelInvoke(UpdatePos);

                if (player != null)
                {
                    if (player.IsConnected)
                        player.Connection.active = true;

                    player.transform.localScale = new Vector3(1, 1, 1);

                    //Reset Triggers
                    if (player.triggers != null)
                    {
                        for (int i = player.triggers.Count - 1; i >= 0; i--)
                        {
                            if (player.triggers[i] is TriggerWorkbench)
                            {
                                player.triggers[i].OnEntityLeave(player);
                                player.triggers.RemoveAt(i);
                            }
                        }
                    }

                    //Reset Workbench Level
                    player.cachedCraftLevel = 0f;
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench1, false);
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench2, false);
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench3, false);
                    player.nextCheckTime = Time.realtimeSinceStartup;
                }

                if (col != null)
                    Destroy(col);
                if (child != null)
                    Destroy(child);

                GameObject.Destroy(this);
            }

        }

        #endregion Monobehaviour

        #region Helpers

        private BasePlayer? GetPlayer(ulong steamID)
        {
            foreach (var player in BasePlayer.allPlayerList)
            {
                if (player.userID.Get() != steamID)
                    continue;

                return player;
            }
            return null;
        }
        private void AddLocalizedCommand(string command)
        {
            foreach (string language in lang.GetLanguages(this))
            {
                Dictionary<string, string> messages = lang.GetMessages(language, this);
                foreach (KeyValuePair<string, string> message in messages)
                {
                    if (!message.Key.Equals(command)) continue;

                    if (string.IsNullOrEmpty(message.Value)) continue;

                    AddCovalenceCommand(message.Value, command);
                }
            }
        }

        private bool HasPerm(string id, string perm) => permission.UserHasPermission(id, perm);

        private string GetLang(string langKey, string playerId = null, params object[] args) => string.Format(lang.GetMessage(langKey, this, playerId), args);

        private void Message(IPlayer player, string langKey, params object[] args)
        {
            if (player.IsConnected) player.Message(GetLang(langKey, player.Id, args));
        }

        private bool IsInvisible(BasePlayer player) => player?._limitedNetworking ?? false;

        private void UnSubscribeFromHooks()
        {
            foreach (var hook in _registeredhooks)
                Unsubscribe(hook);
        }

        private void SubscribeToHooks()
        {
            foreach (var hook in _registeredhooks)
                Subscribe(hook);

        }

        private static void SendEffect(BasePlayer player, string sound) => EffectNetwork.Send(new Effect(sound, player, 0, Vector3.zero, Vector3.forward), player.net.connection);


        #endregion Helpers

        #region Public Helpers
        public void _Disappear(BasePlayer basePlayer) => Disappear(basePlayer);
        public void _Reappear(BasePlayer basePlayer) => Reappear(basePlayer);
        public bool _IsInvisible(BasePlayer basePlayer) => IsInvisible(basePlayer);
        #endregion

        #region Harmony

        //Used for voices/sounds
        [HarmonyPatch(typeof(BaseNetworkable), "GetConnectionsWithin", typeof(Vector3), typeof(float), typeof(bool)), AutoPatch]
        private static class BaseNetworkable_GetConnectionsWithin_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(ref List<Connection> __result, Vector3 position, float distance)
            {
                float distanceSqr = distance * distance;
                foreach (var vanishPlayer in _hiddenPlayers._vanishedPlayers)
                {
                    if (vanishPlayer == null)
                        continue;

                    Transform playerTransform = vanishPlayer.transform;
                    if (playerTransform == null)
                        continue;

                    if ((position - playerTransform.position).sqrMagnitude > distanceSqr) continue;

                    bool alreadyAdded = false;
                    for (int i = __result.Count - 1; i >= 0; i--)
                    {
                        if (__result[i].userid == vanishPlayer.userID)
                        {
                            alreadyAdded = true;
                            __result.RemoveAt(i);
                            break;
                        }
                    }

                    if (!alreadyAdded)
                        __result.Add(vanishPlayer.Connection);
                }
            }
        }

        [HarmonyPatch(typeof(BaseEntity), "SignalBroadcast", typeof(BaseEntity.Signal), typeof(string), typeof(Connection), typeof(string), typeof(float)), AutoPatch]
        private static class BaseEntity_SignalBroadcast_Patch
        {
            [HarmonyPrefix]
            private static bool Prefix(BaseEntity __instance, BaseEntity.Signal signal, string arg, Connection sourceConnection, string fallbackEffect, float maxDistance)
            {
                if (sourceConnection == null)
                    return true;

                if (_hiddenPlayers.IsHidden(sourceConnection.userid))
                    return false;

                return true;
            }
        }

        [HarmonyPatch(typeof(EffectNetwork), "Send", typeof(Effect), typeof(EntityNetworkRange)), AutoPatch]
        private static class EffectNetwork_Send_Patch
        {
            [HarmonyPrefix]
            private static bool Prefix(Effect effect, EntityNetworkRange networkRange)
            {
                if (effect == null || effect.source == 0)
                    return true;

                if (_hiddenPlayers.IsHidden(effect.source))
                    return false;

                return true;
            }
        }

        //ownership stuff
        [HarmonyPatch(typeof(Item), "SetItemOwnership", typeof(BasePlayer), typeof(Translate.Phrase)), AutoPatch]
        private static class Item_SetItemOwnership_phrase_Patch
        {
            [HarmonyPrefix]
            private static bool Prefix(Item __instance, BasePlayer player, Translate.Phrase reason)
            {
                if (player._limitedNetworking)
                {
                    return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(Item), "SetItemOwnership", typeof(BasePlayer), typeof(string)), AutoPatch]
        private static class Item_SetItemOwnership_string_Patch
        {
            [HarmonyPrefix]
            private static bool Prefix(Item __instance, BasePlayer player, string reason)
            {
                if (player._limitedNetworking)
                {
                    return false;
                }
                return true;
            }
        }

        //for shotgun traps
        [HarmonyPatch(typeof(BasePlayer), "Teleport", typeof(Vector3)), AutoPatch]
        private static class BasePlayer_Teleport_Patch
        {
            [HarmonyPrefix]
            private static bool Prefix(BasePlayer __instance, Vector3 position)
            {
                if (__instance._limitedNetworking)
                {
                    __instance.MovePosition(position, false);
                    __instance.ClientRPC(RpcTarget.Player("ForcePositionTo", __instance), position);
                    return false;
                }
                return true;
            }
        }
        #endregion Harmony
    }
}