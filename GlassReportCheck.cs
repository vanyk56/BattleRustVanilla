using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("GlassReportCheck", "OpenAI", "1.4.0")]
    [Description("Glass-style report and moderation check system with freeze, god mode, moderator role, prefix and vanish.")]
    public class GlassReportCheck : RustPlugin
    {
        private const string PermissionModerator = "glassreport.moderator";
        private const string UiModer = "GlassReportCheck.Moder";
        private const string UiRoot = "GlassReportCheck.Root";
        private const string UiCheck = "GlassReportCheck.Check";
        private const string UiModeratorNotice = "GlassReportCheck.ModeratorNotice";
        private const string BlurMaterial = "assets/content/ui/uibackgroundblur-ingamemenu.mat";
        private const string RoundedSprite = "assets/content/ui/ui.rounded.tga";

        private PluginConfig _config;
        private StoredData _data;
        private readonly Dictionary<ulong, ActiveCheck> _activeChecks = new Dictionary<ulong, ActiveCheck>();
        private readonly Dictionary<ulong, PendingVerdict> _pendingVerdicts = new Dictionary<ulong, PendingVerdict>();
        private readonly Dictionary<ulong, string> _searchQueries = new Dictionary<ulong, string>();
        private readonly HashSet<ulong> _vanished = new HashSet<ulong>();

        [PluginReference] private Plugin Vanish;

        #region Configuration

        private class PluginConfig
        {
            [JsonProperty("Команда открытия меню")]
            public string ChatCommand = "report";

            [JsonProperty("Скрывать модераторов из списка жалоб")]
            public bool HideModeratorsFromReportList = true;

            [JsonProperty("Очищать жалобы после завершения проверки")]
            public bool ClearReportsAfterCheck = true;

            [JsonProperty("Игроков на одной странице")]
            public int PlayersPerPage = 8;

            [JsonProperty("Интервал дополнительной фиксации позиции, сек")]
            public float FreezePositionInterval = 0.20f;

            [JsonProperty("Включить звук при вызове на проверку")]
            public bool EnableCheckCallSound = true;

            [JsonProperty("Префаб звука вызова на проверку")]
            public string CheckCallSoundPrefab = "assets/bundled/prefabs/fx/invite_notice.prefab";

            [JsonProperty("Количество пиликаний при вызове")]
            public int CheckCallSoundRepeats = 3;

            [JsonProperty("Интервал между пиликаниями, сек")]
            public float CheckCallSoundInterval = 0.45f;

            [JsonProperty("Сколько часов показывать плашку ПРОВЕРЕН")]
            public int VerifiedBadgeHours = 18;

            [JsonProperty("Команда панели модератора")]
            public string ModerCommand = "moder";

            [JsonProperty("Показывать префикс модератора в чате")]
            public bool EnableChatPrefix = true;

            [JsonProperty("Текст префикса модератора")]
            public string ChatPrefix = "[Moderator]";

            [JsonProperty("Цвет префикса модератора (HEX)")]
            public string ChatPrefixColor = "#FFA500";

            [JsonProperty("Причины жалоб")]
            public List<string> Reasons = new List<string>
            {
                "Читы",
                "Макросы",
                "Другое"
            };

            [JsonProperty("Цвета интерфейса")]
            public UiColors Colors = new UiColors();
        }

        private class UiColors
        {
            [JsonProperty("Затемнение фона")]
            public string Overlay = "0.025 0.030 0.028 0.78";

            [JsonProperty("Стекло")]
            public string Glass = "0.105 0.120 0.108 0.86";

            [JsonProperty("Стекло светлее")]
            public string GlassLight = "0.155 0.170 0.150 0.72";

            [JsonProperty("Нежно-салатовый")]
            public string Lime = "0.690 0.875 0.555 1.00";

            [JsonProperty("Нежно-салатовый прозрачный")]
            public string LimeSoft = "0.690 0.875 0.555 0.18";

            [JsonProperty("Кремовый")]
            public string Cream = "0.965 0.930 0.835 1.00";

            [JsonProperty("Вторичный текст")]
            public string Muted = "0.765 0.780 0.730 0.85";

            [JsonProperty("Красный")]
            public string Danger = "0.900 0.365 0.325 0.92";

            [JsonProperty("Текст на салатовых кнопках")]
            public string ButtonText = "0.075 0.095 0.070 1.00";
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                    throw new Exception("Config is null");
            }
            catch (Exception ex)
            {
                PrintWarning($"Не удалось прочитать конфиг: {ex.Message}. Создаю новый.");
                LoadDefaultConfig();
            }

            _config.Reasons = new List<string>
            {
                "Читы",
                "Макросы",
                "Другое"
            };

            if (_config.Colors == null)
                _config.Colors = new UiColors();
            if (string.IsNullOrWhiteSpace(_config.ModerCommand))
                _config.ModerCommand = "moder";
            if (string.IsNullOrWhiteSpace(_config.ChatPrefix))
                _config.ChatPrefix = "[Moderator]";
            if (string.IsNullOrWhiteSpace(_config.ChatPrefixColor))
                _config.ChatPrefixColor = "#FFA500";

            _config.PlayersPerPage = Mathf.Clamp(_config.PlayersPerPage, 4, 8);
            _config.FreezePositionInterval = Mathf.Clamp(_config.FreezePositionInterval, 0.10f, 1.00f);
            _config.CheckCallSoundRepeats = Mathf.Clamp(_config.CheckCallSoundRepeats, 1, 10);
            _config.CheckCallSoundInterval = Mathf.Clamp(_config.CheckCallSoundInterval, 0.10f, 3.00f);
            _config.VerifiedBadgeHours = Mathf.Clamp(_config.VerifiedBadgeHours, 1, 168);
            if (string.IsNullOrWhiteSpace(_config.CheckCallSoundPrefab))
                _config.CheckCallSoundPrefab = "assets/bundled/prefabs/fx/invite_notice.prefab";
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        #endregion

        #region Data

        private class StoredData
        {
            public Dictionary<ulong, List<ReportRecord>> Reports = new Dictionary<ulong, List<ReportRecord>>();
            public Dictionary<ulong, long> VerifiedUntil = new Dictionary<ulong, long>();
            public Dictionary<ulong, KnownPlayerRecord> Players = new Dictionary<ulong, KnownPlayerRecord>();
        }

        private class KnownPlayerRecord
        {
            public ulong UserId;
            public string Name;
            public long LastSeen;
        }

        private class ReportRecord
        {
            public ulong ReporterId;
            public string ReporterName;
            public ulong TargetId;
            public string TargetName;
            public string Reason;
            public long UnixTime;
        }

        private class ActiveCheck
        {
            public ulong TargetId;
            public string TargetName;
            public ulong ModeratorId;
            public string ModeratorName;
            public Vector3 FrozenPosition;
            public long StartedAt;
            public string Discord;
        }

        private class PendingVerdict
        {
            public ulong TargetId;
            public string VerdictKey;
            public string Reason;
        }

        private void LoadData()
        {
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
            }
            catch
            {
                _data = new StoredData();
            }

            if (_data == null)
                _data = new StoredData();
            if (_data.Reports == null)
                _data.Reports = new Dictionary<ulong, List<ReportRecord>>();
            if (_data.VerifiedUntil == null)
                _data.VerifiedUntil = new Dictionary<ulong, long>();
            if (_data.Players == null)
                _data.Players = new Dictionary<ulong, KnownPlayerRecord>();

            // Миграция: добавляем в базу игроков тех, кто уже встречался в старых репортах.
            foreach (var pair in _data.Reports)
            {
                if (pair.Value == null)
                    continue;

                foreach (var report in pair.Value)
                {
                    if (report == null)
                        continue;
                    RememberKnownPlayer(report.TargetId, report.TargetName, report.UnixTime, false);
                    RememberKnownPlayer(report.ReporterId, report.ReporterName, report.UnixTime, false);
                }
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var expiredId in _data.VerifiedUntil.Where(x => x.Value <= now).Select(x => x.Key).ToList())
                _data.VerifiedUntil.Remove(expiredId);
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, _data);
        }

        private void RememberKnownPlayer(BasePlayer player, bool save = false)
        {
            if (player == null)
                return;

            RememberKnownPlayer(player.userID, player.displayName, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), save);
        }

        private void RememberKnownPlayer(ulong userId, string name, long lastSeen, bool save = false)
        {
            if (userId == 0 || _data == null)
                return;

            if (_data.Players == null)
                _data.Players = new Dictionary<ulong, KnownPlayerRecord>();

            KnownPlayerRecord record;
            if (!_data.Players.TryGetValue(userId, out record) || record == null)
            {
                record = new KnownPlayerRecord { UserId = userId };
                _data.Players[userId] = record;
            }

            record.UserId = userId;
            if (!string.IsNullOrWhiteSpace(name))
                record.Name = name;
            if (lastSeen > record.LastSeen)
                record.LastSeen = lastSeen;

            if (save)
                SaveData();
        }

        private KnownPlayerRecord GetKnownPlayer(ulong userId)
        {
            if (_data == null || _data.Players == null)
                return null;

            KnownPlayerRecord record;
            return _data.Players.TryGetValue(userId, out record) ? record : null;
        }

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            permission.RegisterPermission(PermissionModerator, this);
            LoadData();

            if (!string.IsNullOrWhiteSpace(_config.ChatCommand))
                cmd.AddChatCommand(_config.ChatCommand, this, nameof(CommandReport));
            if (!string.IsNullOrWhiteSpace(_config.ModerCommand))
                cmd.AddChatCommand(_config.ModerCommand, this, nameof(CommandModer));
        }

        private void OnServerInitialized()
        {
            foreach (var player in BasePlayer.activePlayerList)
                RememberKnownPlayer(player);
            foreach (var player in BasePlayer.sleepingPlayerList)
                RememberKnownPlayer(player);

            SaveData();
            timer.Every(Mathf.Min(_config.FreezePositionInterval, 0.10f), EnforceFrozenPlayers);
        }

        private void Unload()
        {
            foreach (var vanishedId in _vanished.ToList())
            {
                var vanishedPlayer = BasePlayer.FindByID(vanishedId);
                if (vanishedPlayer != null)
                    SetVanish(vanishedPlayer, false);
            }
            _vanished.Clear();

            foreach (var player in BasePlayer.activePlayerList.ToList())
            {
                CuiHelper.DestroyUi(player, UiModer);
                CuiHelper.DestroyUi(player, UiRoot);
                CuiHelper.DestroyUi(player, UiCheck);
                CuiHelper.DestroyUi(player, UiModeratorNotice);
            }

            _activeChecks.Clear();
            _pendingVerdicts.Clear();
            _searchQueries.Clear();
            SaveData();
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null)
                return;

            RememberKnownPlayer(player, true);

            if (_vanished.Contains(player.userID))
                SetVanish(player, false);

            CuiHelper.DestroyUi(player, UiModer);
            CuiHelper.DestroyUi(player, UiRoot);
            CuiHelper.DestroyUi(player, UiCheck);
            CuiHelper.DestroyUi(player, UiModeratorNotice);
            _searchQueries.Remove(player.userID);

            ActiveCheck active;
            if (_activeChecks.TryGetValue(player.userID, out active))
            {
                var moderator = BasePlayer.FindByID(active.ModeratorId);
                if (moderator != null && moderator.IsConnected)
                {
                    Reply(moderator, $"<color=#E85D52>{EscapeRichText(player.displayName)}</color> вышел с сервера во время проверки.");
                }
            }
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null)
                return;

            RememberKnownPlayer(player, true);

            timer.Once(1.0f, () =>
            {
                if (player == null || !player.IsConnected)
                    return;

                ActiveCheck active;
                if (!_activeChecks.TryGetValue(player.userID, out active))
                    return;

                player.EnsureDismounted();
                player.Teleport(active.FrozenPosition);
                ShowCheckScreen(player, active);
            });
        }

        // Не блокируем OnPlayerInput целиком: это мешает нормальному открытию чата.
        // Вместо этого отбрасываем игровые тики проверяемого игрока. Чат/команды
        // идут отдельным сетевым путём и остаются доступными.
        private object OnPlayerTick(BasePlayer player, PlayerTick msg, bool wasPlayerStalled)
        {
            if (player == null)
                return null;

            ActiveCheck check;
            if (!_activeChecks.TryGetValue(player.userID, out check))
                return null;

            // Сервер не принимает игровые тики проверяемого. Позиция дополнительно
            // принудительно синхронизируется таймером ниже, чтобы клиент не мог
            // даже визуально "уползти" на локальном предикте.
            return true;
        }

        private object OnEntityTakeDamage(BasePlayer player, HitInfo info)
        {
            if (player == null)
                return null;

            if (_activeChecks.ContainsKey(player.userID))
                return true;

            return null;
        }

        // Префикс [Moderator] в глобальном чате для игроков с правом glassreport.moderator.
        // Если на сервере стоит BetterChat — отключите опцию в конфиге и задайте префикс там.
        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (player == null || string.IsNullOrWhiteSpace(message) || !_config.EnableChatPrefix)
                return null;

            if (channel != ConVar.Chat.ChatChannel.Global)
                return null;

            if (!HasModeratorPermission(player))
                return null;

            var prefix = $"<color={_config.ChatPrefixColor}>{EscapeRichText(_config.ChatPrefix)}</color> {EscapeRichText(player.displayName)}";
            Server.Broadcast(EscapeRichText(message), prefix, player.userID);
            Puts($"[CHAT] {player.displayName}: {message}");
            return true;
        }

        #endregion

        #region Commands

        private void CommandModer(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!IsModerator(player))
            {
                Reply(player, "У вас нет доступа к панели модератора.");
                return;
            }

            if (_activeChecks.ContainsKey(player.userID))
            {
                Reply(player, "Во время проверки панель недоступна.");
                return;
            }

            ShowModerUi(player);
        }

        [ChatCommand("modergive")]
        private void CommandModerGive(BasePlayer admin, string command, string[] args)
        {
            if (admin == null)
                return;

            if (!admin.IsAdmin)
            {
                Reply(admin, "Только администратор может выдавать модерку.");
                return;
            }

            if (args == null || args.Length < 1)
            {
                Reply(admin, "Использование: /modergive <ник или SteamID>");
                return;
            }

            Reply(admin, SetModerator(string.Join(" ", args), true));
        }

        [ChatCommand("moderremove")]
        private void CommandModerRemove(BasePlayer admin, string command, string[] args)
        {
            if (admin == null)
                return;

            if (!admin.IsAdmin)
            {
                Reply(admin, "Только администратор может забирать модерку.");
                return;
            }

            if (args == null || args.Length < 1)
            {
                Reply(admin, "Использование: /moderremove <ник или SteamID>");
                return;
            }

            Reply(admin, SetModerator(string.Join(" ", args), false));
        }

        // Консоль/RCON: glassreport.moder give <ник|steamid> / glassreport.moder remove <ник|steamid>
        [ConsoleCommand("glassreport.moder")]
        private void ConsoleModer(ConsoleSystem.Arg arg)
        {
            var caller = arg.Connection?.player as BasePlayer;
            if (arg.Connection != null && (caller == null || !caller.IsAdmin))
                return;

            var action = arg.GetString(0, string.Empty).ToLowerInvariant();
            var query = GetArgTail(arg, 1, 8).Trim();
            if ((action != "give" && action != "remove") || string.IsNullOrWhiteSpace(query))
            {
                arg.ReplyWith("Использование: glassreport.moder <give|remove> <ник или SteamID>");
                return;
            }

            arg.ReplyWith(Regex.Replace(SetModerator(query, action == "give"), "<.*?>", string.Empty));
        }

        private void CommandReport(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (_activeChecks.ContainsKey(player.userID))
            {
                Reply(player, "Во время проверки меню репортов недоступно.");
                return;
            }

            ShowMainUi(player, false, 0);
        }

        private void SaveDiscordForCheck(BasePlayer player, string value)
        {
            ActiveCheck check;
            if (!_activeChecks.TryGetValue(player.userID, out check))
            {
                Reply(player, "Discord можно отправить только во время проверки.");
                return;
            }

            // Один Discord на одну проверку. После первой успешной отправки
            // повторные submit'ы игнорируются, чтобы нельзя было спамить модератору.
            if (!string.IsNullOrWhiteSpace(check.Discord))
            {
                ShowCheckScreen(player, check);
                Reply(player, $"Discord уже отправлен модератору: <color=#B0DF8E>{EscapeRichText(check.Discord)}</color>. Повторная отправка заблокирована.");
                return;
            }

            var discord = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(discord))
            {
                Reply(player, "Введите ваш Discord в поле в окне проверки и нажмите ENTER.");
                return;
            }

            if (discord.Length > 64)
                discord = discord.Substring(0, 64);

            check.Discord = discord;
            ShowCheckScreen(player, check);
            Reply(player, $"Discord <color=#B0DF8E>{EscapeRichText(discord)}</color> отправлен модератору. Повторно отправить его нельзя.");

            var moderator = BasePlayer.FindByID(check.ModeratorId);
            if (moderator != null && moderator.IsConnected)
            {
                Reply(moderator, $"<color=#B0DF8E>DISCORD ПОЛУЧЕН:</color> игрок <color=#F6EDCF>{EscapeRichText(check.TargetName)}</color> — <color=#B0DF8E>{EscapeRichText(discord)}</color>");
                ShowModeratorDiscordNotice(moderator, check);
            }

            Puts($"[CHECK] Discord received from {check.TargetName} ({check.TargetId}) for moderator {check.ModeratorName}: {discord}");
        }

        [ChatCommand("check")]
        private void CommandCheck(BasePlayer moderator, string command, string[] args)
        {
            if (!IsModerator(moderator))
            {
                Reply(moderator, "У вас нет доступа к вызову на проверку.");
                return;
            }

            if (args == null || args.Length < 1)
            {
                Reply(moderator, "Использование: /check <ник или SteamID>");
                return;
            }

            var query = string.Join(" ", args);
            var target = FindOnlinePlayer(query);
            if (target == null)
            {
                Reply(moderator, "Игрок не найден или найдено несколько совпадений.");
                return;
            }

            StartCheck(moderator, target);
        }

        [ChatCommand("checkend")]
        private void CommandCheckEnd(BasePlayer moderator, string command, string[] args)
        {
            if (!IsModerator(moderator))
            {
                Reply(moderator, "У вас нет доступа к завершению проверки.");
                return;
            }

            if (args == null || args.Length < 1)
            {
                Reply(moderator, "Использование: /checkend <ник или SteamID>");
                return;
            }

            var query = string.Join(" ", args);
            ulong targetId;
            if (ulong.TryParse(query, out targetId) && _activeChecks.ContainsKey(targetId))
            {
                ShowVerdictUi(moderator, targetId);
                return;
            }

            var match = _activeChecks.Values
                .Where(x => x.TargetName != null && x.TargetName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (match.Count != 1)
            {
                Reply(moderator, "Проверяемый игрок не найден или найдено несколько совпадений.");
                return;
            }

            ShowVerdictUi(moderator, match[0].TargetId);
        }

        [ConsoleCommand("glassreport.ui")]
        private void ConsoleUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null)
                return;

            var action = arg.GetString(0, string.Empty).ToLowerInvariant();

            if (action == "close")
            {
                _searchQueries.Remove(player.userID);
                CuiHelper.DestroyUi(player, UiRoot);
                return;
            }

            if (action == "players")
            {
                var page = Mathf.Max(0, arg.GetInt(1, 0));
                ShowMainUi(player, false, page);
                return;
            }

            if (action == "moder")
            {
                if (!IsModerator(player))
                    return;

                ShowModerUi(player);
                return;
            }

            if (action == "vanish")
            {
                if (!IsModerator(player))
                    return;

                SetVanish(player, !IsVanished(player));
                ShowModerUi(player);
                return;
            }

            if (action == "moderation")
            {
                if (!IsModerator(player))
                    return;

                var page = Mathf.Max(0, arg.GetInt(1, 0));
                ShowMainUi(player, true, page);
                return;
            }

            if (action == "search")
            {
                var tab = arg.GetString(1, "players").ToLowerInvariant();
                var moderationTab = tab == "moderation";
                if (moderationTab && !IsModerator(player))
                    moderationTab = false;

                var query = GetArgTail(arg, 2, 32).Trim();
                if (query.Length > 64)
                    query = query.Substring(0, 64);

                if (string.IsNullOrWhiteSpace(query))
                    _searchQueries.Remove(player.userID);
                else
                    _searchQueries[player.userID] = query;

                ShowMainUi(player, moderationTab, 0);
                return;
            }

            if (action == "clearsearch")
            {
                var tab = arg.GetString(1, "players").ToLowerInvariant();
                var moderationTab = tab == "moderation" && IsModerator(player);
                _searchQueries.Remove(player.userID);
                ShowMainUi(player, moderationTab, 0);
                return;
            }

            if (action == "discord")
            {
                var discord = GetArgTail(arg, 1, 32).Trim();
                SaveDiscordForCheck(player, discord);
                return;
            }

            if (action == "reasons")
            {
                ulong targetId;
                if (!ulong.TryParse(arg.GetString(1, "0"), out targetId))
                    return;

                ShowReasonsUi(player, targetId);
                return;
            }

            if (action == "send")
            {
                ulong targetId;
                int reasonIndex;
                if (!ulong.TryParse(arg.GetString(1, "0"), out targetId))
                    return;
                if (!int.TryParse(arg.GetString(2, "-1"), out reasonIndex))
                    return;

                SubmitReport(player, targetId, reasonIndex);
                return;
            }

            if (action == "call")
            {
                if (!IsModerator(player))
                    return;

                ulong targetId;
                if (!ulong.TryParse(arg.GetString(1, "0"), out targetId))
                    return;

                var target = BasePlayer.FindByID(targetId);
                if (target == null || !target.IsConnected)
                {
                    Reply(player, "Игрок уже не в сети.");
                    ShowMainUi(player, true, 0);
                    return;
                }

                StartCheck(player, target);
                return;
            }

            if (action == "end")
            {
                if (!IsModerator(player))
                    return;

                ulong targetId;
                if (!ulong.TryParse(arg.GetString(1, "0"), out targetId))
                    return;

                ShowVerdictUi(player, targetId);
                return;
            }

            if (action == "copyid")
            {
                if (!IsModerator(player))
                    return;

                ulong targetId;
                if (!ulong.TryParse(arg.GetString(1, "0"), out targetId))
                    return;

                ShowSteamIdUi(player, targetId);
                return;
            }

            if (action == "copydiscord")
            {
                if (!IsModerator(player))
                    return;

                ulong targetId;
                if (!ulong.TryParse(arg.GetString(1, "0"), out targetId))
                    return;

                ShowDiscordCopyUi(player, targetId);
                return;
            }

            if (action == "verdict")
            {
                if (!IsModerator(player))
                    return;

                ulong targetId;
                if (!ulong.TryParse(arg.GetString(1, "0"), out targetId))
                    return;

                var verdictKey = arg.GetString(2, string.Empty).ToLowerInvariant();
                HandleVerdictSelection(player, targetId, verdictKey);
                return;
            }

            if (action == "duration")
            {
                if (!IsModerator(player))
                    return;

                ulong targetId;
                if (!ulong.TryParse(arg.GetString(1, "0"), out targetId))
                    return;

                var duration = arg.GetString(2, string.Empty);
                ApplyPendingVerdict(player, targetId, duration);
                return;
            }

            if (action == "customreason")
            {
                if (!IsModerator(player))
                    return;

                ulong targetId;
                if (!ulong.TryParse(arg.GetString(1, "0"), out targetId))
                    return;

                PendingVerdict pending;
                if (!_pendingVerdicts.TryGetValue(player.userID, out pending) || pending.TargetId != targetId)
                    return;

                var reason = GetArgTail(arg, 2, 32).Trim();
                if (reason.Length > 96)
                    reason = reason.Substring(0, 96);
                pending.Reason = reason;
                ShowCustomVerdictUi(player, targetId);
                return;
            }

            if (action == "customduration")
            {
                if (!IsModerator(player))
                    return;

                ulong targetId;
                if (!ulong.TryParse(arg.GetString(1, "0"), out targetId))
                    return;

                var duration = GetArgTail(arg, 2, 4).Trim();
                ApplyPendingVerdict(player, targetId, duration);
                return;
            }
        }

        #endregion

        #region Reports

        private void SubmitReport(BasePlayer reporter, ulong targetId, int reasonIndex)
        {
            if (reporter == null)
                return;

            if (targetId == reporter.userID)
            {
                Reply(reporter, "Нельзя отправить жалобу на самого себя.");
                ShowMainUi(reporter, false, 0);
                return;
            }

            if (reasonIndex < 0 || reasonIndex >= _config.Reasons.Count)
                return;

            var onlineTarget = BasePlayer.FindByID(targetId);
            var knownTarget = GetKnownPlayer(targetId);
            var targetName = onlineTarget != null && onlineTarget.IsConnected
                ? onlineTarget.displayName
                : knownTarget != null ? knownTarget.Name : null;

            if (string.IsNullOrWhiteSpace(targetName))
            {
                Reply(reporter, "Игрок не найден в базе сервера.");
                ShowMainUi(reporter, false, 0);
                return;
            }

            List<ReportRecord> list;
            if (!_data.Reports.TryGetValue(targetId, out list))
            {
                list = new List<ReportRecord>();
                _data.Reports[targetId] = list;
            }

            if (list.Any(x => x.ReporterId == reporter.userID))
            {
                Reply(reporter, "Вы уже отправляли жалобу на этого игрока.");
                ShowMainUi(reporter, false, 0);
                return;
            }

            list.Add(new ReportRecord
            {
                ReporterId = reporter.userID,
                ReporterName = reporter.displayName,
                TargetId = targetId,
                TargetName = targetName,
                Reason = _config.Reasons[reasonIndex],
                UnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            SaveData();
            CuiHelper.DestroyUi(reporter, UiRoot);
            Reply(reporter, $"Жалоба на <color=#B0DF8E>{EscapeRichText(targetName)}</color> отправлена модерации.");
        }

        private int GetReportCount(ulong targetId)
        {
            List<ReportRecord> list;
            return _data.Reports.TryGetValue(targetId, out list) && list != null ? list.Count : 0;
        }

        private bool IsRecentlyVerified(ulong targetId)
        {
            if (_data == null || _data.VerifiedUntil == null)
                return false;

            long until;
            return _data.VerifiedUntil.TryGetValue(targetId, out until) &&
                   until > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        #endregion

        #region Check Logic

        private void StartCheck(BasePlayer moderator, BasePlayer target)
        {
            if (moderator == null || target == null)
                return;

            if (!IsModerator(moderator))
                return;

            if (moderator.userID == target.userID)
            {
                Reply(moderator, "Нельзя вызвать самого себя на проверку.");
                return;
            }

            ActiveCheck existing;
            if (_activeChecks.TryGetValue(target.userID, out existing))
            {
                Reply(moderator, $"Игрок уже находится на проверке у {EscapeRichText(existing.ModeratorName)}.");
                return;
            }

            target.EnsureDismounted();

            var check = new ActiveCheck
            {
                TargetId = target.userID,
                TargetName = target.displayName,
                ModeratorId = moderator.userID,
                ModeratorName = moderator.displayName,
                FrozenPosition = target.transform.position,
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            _activeChecks[target.userID] = check;

            CuiHelper.DestroyUi(target, UiRoot);
            ShowCheckScreen(target, check);
            PlayCheckCallSound(target);
            ShowMainUi(moderator, true, 0);

            Reply(moderator, $"Игрок <color=#B0DF8E>{EscapeRichText(target.displayName)}</color> вызван на проверку. Движение заблокировано, урон отключён.");
            Reply(target, $"Вас вызвал на проверку модератор <color=#B0DF8E>{EscapeRichText(moderator.displayName)}</color>. Укажите Discord в окне проверки.");
            BroadcastCheck($"Игрок <color=#B0DF8E>{EscapeRichText(target.displayName)}</color> вызван на проверку модератором <color=#F6EDCF>{EscapeRichText(moderator.displayName)}</color>.");
        }

        private void PlayCheckCallSound(BasePlayer player)
        {
            if (player == null || !player.IsConnected || !_config.EnableCheckCallSound)
                return;

            var repeats = Mathf.Clamp(_config.CheckCallSoundRepeats, 1, 10);
            var interval = Mathf.Clamp(_config.CheckCallSoundInterval, 0.10f, 3.00f);
            var prefab = _config.CheckCallSoundPrefab;

            for (var i = 0; i < repeats; i++)
            {
                var delay = i * interval;
                timer.Once(delay, () =>
                {
                    if (player == null || !player.IsConnected || !_activeChecks.ContainsKey(player.userID))
                        return;

                    var effect = new Effect();
                    effect.Init(Effect.Type.Generic, player.GetNetworkPosition(), Vector3.zero);
                    effect.pooledString = prefab;
                    EffectNetwork.Send(effect, player.net.connection);
                });
            }
        }

        private void HandleVerdictSelection(BasePlayer moderator, ulong targetId, string verdictKey)
        {
            ActiveCheck check;
            if (!_activeChecks.TryGetValue(targetId, out check))
            {
                Reply(moderator, "Этот игрок уже не находится на проверке.");
                ShowMainUi(moderator, true, 0);
                return;
            }

            if (verdictKey == "clean")
            {
                FinalizeVerdict(moderator, targetId, "ЧИСТ", null, false);
                return;
            }

            var pending = new PendingVerdict { TargetId = targetId, VerdictKey = verdictKey };
            if (verdictKey == "macro")
                pending.Reason = "Макросы";
            else if (verdictKey == "cheats")
                pending.Reason = "Читы";
            else if (verdictKey == "custom")
                pending.Reason = string.Empty;
            else
                return;

            _pendingVerdicts[moderator.userID] = pending;

            if (verdictKey == "custom")
                ShowCustomVerdictUi(moderator, targetId);
            else
                ShowDurationUi(moderator, targetId);
        }

        private void ApplyPendingVerdict(BasePlayer moderator, ulong targetId, string duration)
        {
            PendingVerdict pending;
            if (!_pendingVerdicts.TryGetValue(moderator.userID, out pending) || pending.TargetId != targetId)
            {
                Reply(moderator, "Сначала выберите вердикт.");
                ShowVerdictUi(moderator, targetId);
                return;
            }

            if (pending.VerdictKey == "custom" && string.IsNullOrWhiteSpace(pending.Reason))
            {
                Reply(moderator, "Введите свою причину и нажмите Enter.");
                ShowCustomVerdictUi(moderator, targetId);
                return;
            }

            duration = (duration ?? string.Empty).Trim();
            if (duration.Equals("perm", StringComparison.OrdinalIgnoreCase))
            {
                FinalizeVerdict(moderator, targetId, pending.Reason, "perm", true);
                return;
            }

            if (!IsValidBanDuration(duration))
            {
                Reply(moderator, "Неверный срок. Примеры: 1h, 12h, 3d, 30d, 1M.");
                if (pending.VerdictKey == "custom")
                    ShowCustomVerdictUi(moderator, targetId);
                else
                    ShowDurationUi(moderator, targetId);
                return;
            }

            FinalizeVerdict(moderator, targetId, pending.Reason, duration, true);
        }

        private void FinalizeVerdict(BasePlayer moderator, ulong targetId, string verdict, string duration, bool banPlayer)
        {
            ActiveCheck check;
            if (!_activeChecks.TryGetValue(targetId, out check))
            {
                Reply(moderator, "Этот игрок уже не находится на проверке.");
                ShowMainUi(moderator, true, 0);
                return;
            }

            _activeChecks.Remove(targetId);
            _pendingVerdicts.Remove(moderator.userID);

            var target = BasePlayer.FindByID(targetId);
            if (target != null && target.IsConnected)
                CuiHelper.DestroyUi(target, UiCheck);

            if (_config.ClearReportsAfterCheck)
                _data.Reports.Remove(targetId);

            if (!banPlayer)
            {
                var verifiedUntil = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (_config.VerifiedBadgeHours * 3600L);
                _data.VerifiedUntil[targetId] = verifiedUntil;
                SaveData();

                if (target != null && target.IsConnected)
                    Reply(target, $"<color=#B0DF8E>Проверка завершена. Вердикт: ЧИСТ.</color> Плашка <color=#B0DF8E>ПРОВЕРЕН</color> будет отображаться {_config.VerifiedBadgeHours} ч. Репорты на вас при этом остаются доступны.");

                BroadcastCheck($"Проверка игрока <color=#B0DF8E>{EscapeRichText(check.TargetName)}</color> завершена. Вердикт: <color=#B0DF8E>ЧИСТ</color>. Статус ПРОВЕРЕН: {_config.VerifiedBadgeHours} ч. Модератор: <color=#F6EDCF>{EscapeRichText(moderator.displayName)}</color>.");
                Reply(moderator, $"Проверка {EscapeRichText(check.TargetName)} завершена: ЧИСТ. Плашка ПРОВЕРЕН выдана на {_config.VerifiedBadgeHours} ч.");
                ShowMainUi(moderator, true, 0);
                return;
            }

            _data.VerifiedUntil.Remove(targetId);
            SaveData();

            var isPermanent = duration.Equals("perm", StringComparison.OrdinalIgnoreCase);
            var durationLabel = isPermanent ? "навсегда" : FormatDuration(duration);
            var banReason = $"Проверка: {verdict}";

            if (isPermanent)
                Server.Command("banid", targetId.ToString(), check.TargetName ?? targetId.ToString(), banReason);
            else
                Server.Command("banid", targetId.ToString(), check.TargetName ?? targetId.ToString(), banReason, duration);

            if (target != null && target.IsConnected)
                target.Kick($"Вы заблокированы по итогам проверки. Причина: {verdict}. Срок: {durationLabel}");

            BroadcastCheck($"Игрок <color=#B0DF8E>{EscapeRichText(check.TargetName)}</color> заблокирован по итогам проверки. Вердикт: <color=#E85D52>{EscapeRichText(verdict)}</color>. Срок: <color=#F6EDCF>{EscapeRichText(durationLabel)}</color>. Модератор: <color=#F6EDCF>{EscapeRichText(moderator.displayName)}</color>.");
            Reply(moderator, $"Игрок {EscapeRichText(check.TargetName)} заблокирован. Причина: {EscapeRichText(verdict)}. Срок: {EscapeRichText(durationLabel)}.");
            ShowMainUi(moderator, true, 0);
        }

        private void EnforceFrozenPlayers()
        {
            if (_activeChecks.Count == 0)
                return;

            foreach (var pair in _activeChecks.ToArray())
            {
                var player = BasePlayer.FindByID(pair.Key);
                if (player == null || !player.IsConnected || player.IsDead())
                    continue;

                var check = pair.Value;
                player.EnsureDismounted();
                player.Teleport(check.FrozenPosition);
            }
        }

        #endregion

        #region UI

        private void ShowMainUi(BasePlayer player, bool moderationTab, int requestedPage)
        {
            CuiHelper.DestroyUi(player, UiRoot);

            var isModerator = IsModerator(player);
            if (!isModerator)
                moderationTab = false;

            // Перед построением меню синхронизируем базу с текущими online/sleeping игроками.
            foreach (var activePlayer in BasePlayer.activePlayerList)
                RememberKnownPlayer(activePlayer);
            foreach (var sleepingPlayer in BasePlayer.sleepingPlayerList)
                RememberKnownPlayer(sleepingPlayer);

            string searchQuery;
            if (!_searchQueries.TryGetValue(player.userID, out searchQuery))
                searchQuery = string.Empty;
            searchQuery = (searchQuery ?? string.Empty).Trim();

            var filteredPlayers = (_data.Players ?? new Dictionary<ulong, KnownPlayerRecord>())
                .Values
                .Where(x => x != null && x.UserId != 0 && x.UserId != player.userID)
                .Where(x => string.IsNullOrWhiteSpace(searchQuery)
                    || (x.Name ?? string.Empty).IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0
                    || x.UserId.ToString().IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0);

            // В модерации главный приоритет — количество жалоб. При равном количестве
            // online идёт выше offline, затем сортировка по нику.
            var source = moderationTab
                ? filteredPlayers
                    .OrderByDescending(x => GetReportCount(x.UserId))
                    .ThenByDescending(x => IsPlayerOnline(x.UserId))
                    .ThenBy(x => x.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : filteredPlayers
                    .OrderByDescending(x => IsPlayerOnline(x.UserId))
                    .ThenBy(x => x.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            var perPage = Mathf.Clamp(_config.PlayersPerPage, 4, 8);
            var totalPages = Math.Max(1, (int)Math.Ceiling(source.Count / (double)perPage));
            var page = Mathf.Clamp(requestedPage, 0, totalPages - 1);
            var pagePlayers = source.Skip(page * perPage).Take(perPage).ToList();

            var c = new CuiElementContainer();

            c.Add(new CuiPanel
            {
                Image = { Color = _config.Colors.Overlay },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true,
                KeyboardEnabled = true
            }, "Overlay", UiRoot);

            c.Add(new CuiPanel
            {
                Image = { Color = _config.Colors.Glass, Material = BlurMaterial, Sprite = RoundedSprite, ImageType = UnityEngine.UI.Image.Type.Sliced },
                RectTransform = { AnchorMin = "0.150 0.060", AnchorMax = "0.850 0.940" }
            }, UiRoot, UiRoot + ".Window");

            c.Add(new CuiPanel
            {
                Image = { Color = _config.Colors.Lime, Sprite = RoundedSprite, ImageType = UnityEngine.UI.Image.Type.Sliced },
                RectTransform = { AnchorMin = "0.020 0.988", AnchorMax = "0.980 0.998" }
            }, UiRoot + ".Window", UiRoot + ".Accent");

            AddLabel(c, UiRoot + ".Window", "Репорты", 28, "0.045 0.910", "0.64 0.975", _config.Colors.Cream, TextAnchor.MiddleLeft, true);
            AddLabel(c, UiRoot + ".Window", moderationTab ? "МОДЕРАЦИЯ И ПРОВЕРКИ" : "ЖАЛОБЫ НА ИГРОКОВ", 14,
                "0.045 0.858", "0.64 0.910", _config.Colors.Muted, TextAnchor.MiddleLeft, false);

            AddButton(c, UiRoot + ".Window", "ИГРОКИ", moderationTab ? "0.690 0.875 0.555 0.76" : _config.Colors.Lime,
                "0.045 0.802", isModerator ? "0.275 0.852" : "0.365 0.852", "glassreport.ui players 0", _config.Colors.ButtonText, 14);

            if (isModerator)
            {
                AddButton(c, UiRoot + ".Window", "МОДЕРАЦИЯ", moderationTab ? _config.Colors.Lime : "0.690 0.875 0.555 0.76",
                    "0.290 0.802", "0.535 0.852", "glassreport.ui moderation 0", _config.Colors.ButtonText, 14);
            }

            if (isModerator)
            {
                AddButton(c, UiRoot + ".Window", "ПАНЕЛЬ", "0.690 0.875 0.555 0.76",
                    "0.550 0.802", "0.690 0.852", "glassreport.ui moder", _config.Colors.ButtonText, 14);
            }

            AddButton(c, UiRoot + ".Window", "×", _config.Colors.Lime, "0.925 0.915", "0.970 0.962", "glassreport.ui close", _config.Colors.ButtonText, 23);

            var tabName = moderationTab ? "moderation" : "players";
            AddLabel(c, UiRoot + ".Window", "ПОИСК ПО НИКУ ИЛИ STEAM ID", 11,
                "0.045 0.755", "0.500 0.795", _config.Colors.Muted, TextAnchor.MiddleLeft, false);
            AddInputField(c, UiRoot + ".Window", searchQuery, 14, 64, false, $"glassreport.ui search {tabName}",
                "0.045 0.705", "0.790 0.755", TextAnchor.MiddleLeft, false);
            AddButton(c, UiRoot + ".Window", "СБРОСИТЬ", _config.Colors.Lime,
                "0.805 0.705", "0.955 0.755", $"glassreport.ui clearsearch {tabName}", _config.Colors.ButtonText, 12);

            if (pagePlayers.Count == 0)
            {
                var emptyText = string.IsNullOrWhiteSpace(searchQuery)
                    ? "Нет игроков в базе сервера"
                    : "По этому запросу никого не найдено";
                AddLabel(c, UiRoot + ".Window", emptyText, 18,
                    "0.10 0.38", "0.90 0.61", _config.Colors.Muted, TextAnchor.MiddleCenter, false);
            }
            else
            {
                const int columns = 4;
                const float left = 0.045f;
                const float right = 0.955f;
                const float gapX = 0.016f;
                const float top = 0.670f;
                const float cardHeight = 0.245f;
                const float gapY = 0.018f;
                var cardWidth = (right - left - gapX * (columns - 1)) / columns;

                for (var i = 0; i < pagePlayers.Count; i++)
                {
                    var target = pagePlayers[i];
                    var online = IsPlayerOnline(target.UserId);
                    var col = i % columns;
                    var row = i / columns;
                    var xMin = left + col * (cardWidth + gapX);
                    var xMax = xMin + cardWidth;
                    var yMax = top - row * (cardHeight + gapY);
                    var yMin = yMax - cardHeight;
                    var cardName = UiRoot + ".Card." + i;

                    c.Add(new CuiPanel
                    {
                        Image = { Color = row % 2 == 0 ? "0.19 0.205 0.185 0.62" : "0.15 0.165 0.148 0.60", Material = BlurMaterial, Sprite = RoundedSprite, ImageType = UnityEngine.UI.Image.Type.Sliced },
                        RectTransform = { AnchorMin = $"{F(xMin)} {F(yMin)}", AnchorMax = $"{F(xMax)} {F(yMax)}" }
                    }, UiRoot + ".Window", cardName);

                    var isVerified = IsRecentlyVerified(target.UserId);
                    var onlineText = online ? "● ОНЛАЙН" : "● ОФЛАЙН";
                    if (moderationTab)
                    {
                        var reportCount = GetReportCount(target.UserId);
                        ActiveCheck active;
                        var isOnCheck = _activeChecks.TryGetValue(target.UserId, out active);
                        var status = isOnCheck
                            ? (string.IsNullOrWhiteSpace(active.Discord) ? "НА ПРОВЕРКЕ  •  ОНЛАЙН" : "DISCORD: " + TrimName(active.Discord, 12))
                            : (isVerified
                                ? "✓ ПРОВЕРЕН  •  " + onlineText + "  •  ЖАЛОБ: " + reportCount
                                : onlineText + "  •  ЖАЛОБ: " + reportCount);
                        AddLabel(c, cardName, EscapeRichText(status), 9, "0.04 0.900", "0.96 0.985",
                            online || isOnCheck || isVerified ? _config.Colors.Lime : _config.Colors.Muted, TextAnchor.MiddleCenter, true);
                    }
                    else
                    {
                        var status = isVerified ? "✓ ПРОВЕРЕН  •  " + onlineText : onlineText;
                        AddLabel(c, cardName, EscapeRichText(status), 9, "0.04 0.900", "0.96 0.985",
                            online || isVerified ? _config.Colors.Lime : _config.Colors.Muted, TextAnchor.MiddleCenter, true);
                    }

                    AddPlayerAvatar(c, cardName, target.UserId, "0.335 0.535", "0.665 0.885");

                    AddLabel(c, cardName, EscapeRichText(TrimName(target.Name, 20)), 14,
                        "0.05 0.405", "0.95 0.520", _config.Colors.Cream, TextAnchor.MiddleCenter, true);

                    AddLabel(c, cardName, "STEAM ID  •  CTRL+A / CTRL+C", 9,
                        "0.06 0.330", "0.94 0.405", _config.Colors.Muted, TextAnchor.MiddleCenter, false);
                    AddInputField(c, cardName, target.UserId.ToString(), 11, 24, true, string.Empty,
                        "0.08 0.225", "0.92 0.330", TextAnchor.MiddleCenter, false);

                    if (moderationTab)
                    {
                        ActiveCheck activeCardCheck;
                        if (_activeChecks.TryGetValue(target.UserId, out activeCardCheck))
                        {
                            if (!string.IsNullOrWhiteSpace(activeCardCheck.Discord))
                            {
                                AddButton(c, cardName, "DISCORD", _config.Colors.Lime,
                                    "0.08 0.055", "0.48 0.185", $"glassreport.ui copydiscord {target.UserId}", _config.Colors.ButtonText, 10);
                                AddButton(c, cardName, "ЗАВЕРШИТЬ", _config.Colors.Lime,
                                    "0.52 0.055", "0.92 0.185", $"glassreport.ui end {target.UserId}", _config.Colors.ButtonText, 10);
                            }
                            else
                            {
                                AddButton(c, cardName, "ЗАВЕРШИТЬ", _config.Colors.Lime,
                                    "0.08 0.055", "0.92 0.185", $"glassreport.ui end {target.UserId}", _config.Colors.ButtonText, 11);
                            }
                        }
                        else if (online)
                        {
                            AddButton(c, cardName, "НА ПРОВЕРКУ", _config.Colors.Lime,
                                "0.08 0.055", "0.92 0.185", $"glassreport.ui call {target.UserId}", _config.Colors.ButtonText, 11);
                        }
                        else
                        {
                            AddButton(c, cardName, "ОФЛАЙН", "0.24 0.27 0.24 0.82",
                                "0.08 0.055", "0.92 0.185", string.Empty, _config.Colors.Cream, 11);
                        }
                    }
                    else
                    {
                        AddButton(c, cardName, "ПОЖАЛОВАТЬСЯ", _config.Colors.Lime,
                            "0.08 0.055", "0.92 0.185", $"glassreport.ui reasons {target.UserId}", _config.Colors.ButtonText, 11);
                    }
                }
            }

            if (page > 0)
            {
                AddButton(c, UiRoot + ".Window", "‹", _config.Colors.Lime,
                    "0.045 0.045", "0.100 0.100", $"glassreport.ui {tabName} {page - 1}", _config.Colors.ButtonText, 25);
            }

            AddLabel(c, UiRoot + ".Window", $"{page + 1} / {totalPages}", 13,
                "0.425 0.045", "0.575 0.100", _config.Colors.Muted, TextAnchor.MiddleCenter, false);

            if (page + 1 < totalPages)
            {
                AddButton(c, UiRoot + ".Window", "›", _config.Colors.Lime,
                    "0.900 0.045", "0.955 0.100", $"glassreport.ui {tabName} {page + 1}", _config.Colors.ButtonText, 25);
            }

            CuiHelper.AddUi(player, c);
        }

        private void ShowReasonsUi(BasePlayer player, ulong targetId)
        {
            var onlineTarget = BasePlayer.FindByID(targetId);
            var knownTarget = GetKnownPlayer(targetId);
            var targetName = onlineTarget != null && onlineTarget.IsConnected
                ? onlineTarget.displayName
                : knownTarget != null ? knownTarget.Name : null;

            if (string.IsNullOrWhiteSpace(targetName))
            {
                Reply(player, "Игрок не найден в базе сервера.");
                ShowMainUi(player, false, 0);
                return;
            }

            CuiHelper.DestroyUi(player, UiRoot);
            var c = CreateModalRoot(player, "0.300 0.235", "0.700 0.765", out var window);

            AddLabel(c, window, "ВЫБЕРИТЕ ПРИЧИНУ ЖАЛОБЫ", 23,
                "0.07 0.830", "0.93 0.940", _config.Colors.Cream, TextAnchor.MiddleCenter, true);
            AddLabel(c, window, EscapeRichText(TrimName(targetName, 32)), 15,
                "0.07 0.755", "0.93 0.825", _config.Colors.Lime, TextAnchor.MiddleCenter, false);

            AddButton(c, window, "ЧИТЫ", _config.Colors.Lime,
                "0.10 0.585", "0.90 0.690", $"glassreport.ui send {targetId} 0", _config.Colors.ButtonText, 15);
            AddButton(c, window, "МАКРОСЫ", _config.Colors.Lime,
                "0.10 0.440", "0.90 0.545", $"glassreport.ui send {targetId} 1", _config.Colors.ButtonText, 15);
            AddButton(c, window, "ДРУГОЕ", _config.Colors.Lime,
                "0.10 0.295", "0.90 0.400", $"glassreport.ui send {targetId} 2", _config.Colors.ButtonText, 15);

            AddButton(c, window, "НАЗАД", _config.Colors.Lime,
                "0.10 0.095", "0.90 0.185", "glassreport.ui players 0", _config.Colors.ButtonText, 13);

            CuiHelper.AddUi(player, c);
        }

        private void ShowCheckScreen(BasePlayer target, ActiveCheck check)
        {
            CuiHelper.DestroyUi(target, UiCheck);

            var c = new CuiElementContainer();

            c.Add(new CuiPanel
            {
                Image = { Color = "0.025 0.030 0.028 0.54" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = false
            }, "Overlay", UiCheck);

            c.Add(new CuiPanel
            {
                Image = { Color = _config.Colors.Glass, Material = BlurMaterial, Sprite = RoundedSprite, ImageType = UnityEngine.UI.Image.Type.Sliced },
                RectTransform = { AnchorMin = "0.175 0.190", AnchorMax = "0.825 0.810" }
            }, UiCheck, UiCheck + ".Window");

            c.Add(new CuiPanel
            {
                Image = { Color = _config.Colors.Lime, Sprite = RoundedSprite, ImageType = UnityEngine.UI.Image.Type.Sliced },
                RectTransform = { AnchorMin = "0.025 0.976", AnchorMax = "0.975 0.990" }
            }, UiCheck + ".Window", UiCheck + ".Accent");

            AddLabel(c, UiCheck + ".Window", "ВАС ВЫЗВАЛИ НА ПРОВЕРКУ", 34,
                "0.06 0.765", "0.94 0.920", _config.Colors.Cream, TextAnchor.MiddleCenter, true);

            AddLabel(c, UiCheck + ".Window", "Не выходите с сервера и ожидайте указаний модератора.", 18,
                "0.08 0.650", "0.92 0.760", _config.Colors.Muted, TextAnchor.MiddleCenter, false);

            AddLabel(c, UiCheck + ".Window", "Укажите свой Discord, чтобы модератор мог связаться с вами.", 18,
                "0.08 0.565", "0.92 0.650", _config.Colors.Cream, TextAnchor.MiddleCenter, false);

            var hasDiscord = !string.IsNullOrWhiteSpace(check.Discord);
            AddLabel(c, UiCheck + ".Window",
                hasDiscord ? "Discord уже отправлен. Повторная отправка заблокирована" : "Введите Discord в поле ниже и нажмите ENTER",
                15, "0.14 0.495", "0.86 0.565", _config.Colors.Lime, TextAnchor.MiddleCenter, true);

            if (hasDiscord)
            {
                AddInputField(c, UiCheck + ".Window", check.Discord, 18, 64, true, string.Empty,
                    "0.20 0.400", "0.80 0.485", TextAnchor.MiddleCenter, false);
            }
            else
            {
                AddInputField(c, UiCheck + ".Window", string.Empty, 18, 64, false, "glassreport.ui discord",
                    "0.20 0.400", "0.80 0.485", TextAnchor.MiddleCenter, false);
            }

            var discordStatus = !hasDiscord
                ? "Discord ещё не отправлен"
                : $"✓ Discord отправлен: <color=#B0DF8E>{EscapeRichText(check.Discord)}</color>";
            AddLabel(c, UiCheck + ".Window", discordStatus, 15,
                "0.08 0.315", "0.92 0.380", _config.Colors.Muted, TextAnchor.MiddleCenter, false);

            AddLabel(c, UiCheck + ".Window", $"Модератор: <color=#B0DF8E>{EscapeRichText(check.ModeratorName)}</color>", 17,
                "0.08 0.235", "0.92 0.310", _config.Colors.Cream, TextAnchor.MiddleCenter, false);

            AddLabel(c, UiCheck + ".Window", "ДВИЖЕНИЕ ЗАБЛОКИРОВАНО   •   НЕУЯЗВИМОСТЬ ВКЛЮЧЕНА", 13,
                "0.06 0.090", "0.94 0.195", _config.Colors.Lime, TextAnchor.MiddleCenter, true);

            CuiHelper.AddUi(target, c);
        }

        private void ShowModeratorDiscordNotice(BasePlayer moderator, ActiveCheck check)
        {
            if (moderator == null || !moderator.IsConnected || check == null)
                return;

            CuiHelper.DestroyUi(moderator, UiModeratorNotice);
            var c = new CuiElementContainer();

            c.Add(new CuiPanel
            {
                Image = { Color = "0.075 0.090 0.078 0.96", Material = BlurMaterial, Sprite = RoundedSprite, ImageType = UnityEngine.UI.Image.Type.Sliced },
                RectTransform = { AnchorMin = "0.650 0.790", AnchorMax = "0.955 0.945" },
                CursorEnabled = false
            }, "Overlay", UiModeratorNotice);

            c.Add(new CuiPanel
            {
                Image = { Color = _config.Colors.Lime, Sprite = RoundedSprite, ImageType = UnityEngine.UI.Image.Type.Sliced },
                RectTransform = { AnchorMin = "0.025 0.930", AnchorMax = "0.975 0.975" }
            }, UiModeratorNotice);

            AddLabel(c, UiModeratorNotice, "DISCORD ПОЛУЧЕН", 17,
                "0.06 0.590", "0.94 0.900", _config.Colors.Lime, TextAnchor.MiddleCenter, true);
            AddLabel(c, UiModeratorNotice, EscapeRichText(check.TargetName), 14,
                "0.06 0.390", "0.94 0.610", _config.Colors.Cream, TextAnchor.MiddleCenter, true);
            AddLabel(c, UiModeratorNotice, EscapeRichText(check.Discord), 16,
                "0.06 0.180", "0.94 0.410", _config.Colors.Cream, TextAnchor.MiddleCenter, false);
            AddLabel(c, UiModeratorNotice, "Копирование: /report → МОДЕРАЦИЯ → DISCORD", 10,
                "0.06 0.060", "0.94 0.190", _config.Colors.Muted, TextAnchor.MiddleCenter, false);

            CuiHelper.AddUi(moderator, c);
            timer.Once(10f, () =>
            {
                if (moderator != null && moderator.IsConnected)
                    CuiHelper.DestroyUi(moderator, UiModeratorNotice);
            });
        }

        private void ShowVerdictUi(BasePlayer moderator, ulong targetId)
        {
            if (!IsModerator(moderator))
                return;

            ActiveCheck check;
            if (!_activeChecks.TryGetValue(targetId, out check))
            {
                Reply(moderator, "Этот игрок сейчас не находится на проверке.");
                ShowMainUi(moderator, true, 0);
                return;
            }

            CuiHelper.DestroyUi(moderator, UiRoot);
            _pendingVerdicts.Remove(moderator.userID);

            var c = CreateModalRoot(moderator, "0.265 0.205", "0.735 0.795", out var window);
            AddLabel(c, window, "ВЫНЕСТИ ВЕРДИКТ", 27, "0.07 0.840", "0.93 0.940", _config.Colors.Cream, TextAnchor.MiddleCenter, true);
            AddLabel(c, window, EscapeRichText(check.TargetName), 17, "0.07 0.780", "0.93 0.840", _config.Colors.Lime, TextAnchor.MiddleCenter, false);
            AddLabel(c, window, $"SteamID: {targetId}", 13, "0.07 0.725", "0.93 0.780", _config.Colors.Muted, TextAnchor.MiddleCenter, false);

            AddButton(c, window, "МАКРОСЫ — ЗАБЛОКИРОВАТЬ", _config.Colors.Lime,
                "0.10 0.590", "0.90 0.675", $"glassreport.ui verdict {targetId} macro", _config.Colors.ButtonText, 15);
            AddButton(c, window, "ЧИТЫ — ЗАБЛОКИРОВАТЬ", _config.Colors.Lime,
                "0.10 0.485", "0.90 0.570", $"glassreport.ui verdict {targetId} cheats", _config.Colors.ButtonText, 15);
            AddButton(c, window, "ЧИСТ", _config.Colors.Lime,
                "0.10 0.380", "0.90 0.465", $"glassreport.ui verdict {targetId} clean", _config.Colors.ButtonText, 15);
            AddButton(c, window, "СВОЯ ПРИЧИНА", _config.Colors.Lime,
                "0.10 0.275", "0.90 0.360", $"glassreport.ui verdict {targetId} custom", _config.Colors.ButtonText, 15);
            AddButton(c, window, "НАЗАД", _config.Colors.Lime,
                "0.10 0.095", "0.90 0.175", "glassreport.ui moderation 0", _config.Colors.ButtonText, 13);

            CuiHelper.AddUi(moderator, c);
        }

        private void ShowDurationUi(BasePlayer moderator, ulong targetId)
        {
            PendingVerdict pending;
            ActiveCheck check;
            if (!_pendingVerdicts.TryGetValue(moderator.userID, out pending) || pending.TargetId != targetId ||
                !_activeChecks.TryGetValue(targetId, out check))
            {
                ShowVerdictUi(moderator, targetId);
                return;
            }

            CuiHelper.DestroyUi(moderator, UiRoot);
            var c = CreateModalRoot(moderator, "0.250 0.175", "0.750 0.825", out var window);
            AddLabel(c, window, "ВЫБЕРИТЕ СРОК БЛОКИРОВКИ", 25, "0.07 0.855", "0.93 0.945", _config.Colors.Cream, TextAnchor.MiddleCenter, true);
            AddLabel(c, window, $"Вердикт: <color=#B0DF8E>{EscapeRichText(pending.Reason)}</color>  •  {EscapeRichText(check.TargetName)}", 15,
                "0.07 0.785", "0.93 0.850", _config.Colors.Muted, TextAnchor.MiddleCenter, false);

            AddButton(c, window, "1 ЧАС", _config.Colors.Lime, "0.09 0.645", "0.47 0.725", $"glassreport.ui duration {targetId} 1h", _config.Colors.ButtonText, 14);
            AddButton(c, window, "1 ДЕНЬ", _config.Colors.Lime, "0.53 0.645", "0.91 0.725", $"glassreport.ui duration {targetId} 1d", _config.Colors.ButtonText, 14);
            AddButton(c, window, "7 ДНЕЙ", _config.Colors.Lime, "0.09 0.540", "0.47 0.620", $"glassreport.ui duration {targetId} 7d", _config.Colors.ButtonText, 14);
            AddButton(c, window, "30 ДНЕЙ", _config.Colors.Lime, "0.53 0.540", "0.91 0.620", $"glassreport.ui duration {targetId} 30d", _config.Colors.ButtonText, 14);
            AddButton(c, window, "НАВСЕГДА", _config.Colors.Lime, "0.09 0.435", "0.91 0.515", $"glassreport.ui duration {targetId} perm", _config.Colors.ButtonText, 14);

            AddLabel(c, window, "Свой срок — введите и нажмите Enter. Примеры: 12h, 3d, 1M", 12,
                "0.09 0.330", "0.91 0.405", _config.Colors.Muted, TextAnchor.MiddleCenter, false);
            AddInputField(c, window, "", 16, 16, false, $"glassreport.ui customduration {targetId}",
                "0.18 0.245", "0.82 0.325", TextAnchor.MiddleCenter, false);

            AddButton(c, window, "НАЗАД К ВЕРДИКТУ", _config.Colors.Lime,
                "0.09 0.085", "0.91 0.165", $"glassreport.ui end {targetId}", _config.Colors.ButtonText, 13);
            CuiHelper.AddUi(moderator, c);
        }

        private void ShowCustomVerdictUi(BasePlayer moderator, ulong targetId)
        {
            PendingVerdict pending;
            ActiveCheck check;
            if (!_pendingVerdicts.TryGetValue(moderator.userID, out pending) || pending.TargetId != targetId ||
                !_activeChecks.TryGetValue(targetId, out check))
            {
                ShowVerdictUi(moderator, targetId);
                return;
            }

            CuiHelper.DestroyUi(moderator, UiRoot);
            var c = CreateModalRoot(moderator, "0.245 0.145", "0.755 0.855", out var window);
            AddLabel(c, window, "СВОЙ ВЕРДИКТ", 26, "0.07 0.865", "0.93 0.950", _config.Colors.Cream, TextAnchor.MiddleCenter, true);
            AddLabel(c, window, EscapeRichText(check.TargetName), 15, "0.07 0.810", "0.93 0.865", _config.Colors.Lime, TextAnchor.MiddleCenter, false);
            AddLabel(c, window, "Введите причину и нажмите Enter:", 13, "0.09 0.725", "0.91 0.790", _config.Colors.Muted, TextAnchor.MiddleLeft, false);
            AddInputField(c, window, pending.Reason ?? "", 16, 96, false, $"glassreport.ui customreason {targetId}",
                "0.09 0.635", "0.91 0.715", TextAnchor.MiddleLeft, false);

            var currentReason = string.IsNullOrWhiteSpace(pending.Reason) ? "Причина ещё не сохранена" : $"Причина: {EscapeRichText(pending.Reason)}";
            AddLabel(c, window, currentReason, 12, "0.09 0.565", "0.91 0.625", string.IsNullOrWhiteSpace(pending.Reason) ? _config.Colors.Muted : _config.Colors.Lime, TextAnchor.MiddleLeft, false);

            AddLabel(c, window, "После сохранения причины выберите срок:", 13, "0.09 0.490", "0.91 0.550", _config.Colors.Muted, TextAnchor.MiddleLeft, false);
            AddButton(c, window, "1 ЧАС", _config.Colors.Lime, "0.09 0.390", "0.47 0.465", $"glassreport.ui duration {targetId} 1h", _config.Colors.ButtonText, 13);
            AddButton(c, window, "1 ДЕНЬ", _config.Colors.Lime, "0.53 0.390", "0.91 0.465", $"glassreport.ui duration {targetId} 1d", _config.Colors.ButtonText, 13);
            AddButton(c, window, "7 ДНЕЙ", _config.Colors.Lime, "0.09 0.295", "0.47 0.370", $"glassreport.ui duration {targetId} 7d", _config.Colors.ButtonText, 13);
            AddButton(c, window, "30 ДНЕЙ", _config.Colors.Lime, "0.53 0.295", "0.91 0.370", $"glassreport.ui duration {targetId} 30d", _config.Colors.ButtonText, 13);
            AddButton(c, window, "НАВСЕГДА", _config.Colors.Lime, "0.09 0.200", "0.91 0.275", $"glassreport.ui duration {targetId} perm", _config.Colors.ButtonText, 13);

            AddLabel(c, window, "Свой срок: 12h / 3d / 1M — Enter применит блокировку", 11,
                "0.09 0.135", "0.91 0.195", _config.Colors.Muted, TextAnchor.MiddleCenter, false);
            AddInputField(c, window, "", 14, 16, false, $"glassreport.ui customduration {targetId}",
                "0.24 0.065", "0.76 0.130", TextAnchor.MiddleCenter, false);
            CuiHelper.AddUi(moderator, c);
        }


        private void ShowDiscordCopyUi(BasePlayer moderator, ulong targetId)
        {
            if (!IsModerator(moderator))
                return;

            ActiveCheck check;
            if (!_activeChecks.TryGetValue(targetId, out check) || check == null || string.IsNullOrWhiteSpace(check.Discord))
            {
                Reply(moderator, "Игрок ещё не отправил Discord или проверка уже завершена.");
                ShowMainUi(moderator, true, 0);
                return;
            }

            CuiHelper.DestroyUi(moderator, UiRoot);
            var c = CreateModalRoot(moderator, "0.300 0.285", "0.700 0.715", out var window);
            AddLabel(c, window, "DISCORD ИГРОКА", 26,
                "0.08 0.775", "0.92 0.905", _config.Colors.Cream, TextAnchor.MiddleCenter, true);
            AddLabel(c, window, EscapeRichText(check.TargetName), 14,
                "0.08 0.690", "0.92 0.770", _config.Colors.Lime, TextAnchor.MiddleCenter, false);
            AddLabel(c, window, "Нажмите на поле → Ctrl+A → Ctrl+C", 13,
                "0.08 0.575", "0.92 0.655", _config.Colors.Muted, TextAnchor.MiddleCenter, false);
            AddInputField(c, window, check.Discord, 20, 64, true, string.Empty,
                "0.10 0.405", "0.90 0.555", TextAnchor.MiddleCenter, true);
            AddButton(c, window, "ВЕРНУТЬСЯ", _config.Colors.Lime,
                "0.10 0.145", "0.90 0.270", "glassreport.ui moderation 0", _config.Colors.ButtonText, 14);
            CuiHelper.AddUi(moderator, c);
        }

        private void ShowSteamIdUi(BasePlayer moderator, ulong targetId)
        {
            if (!IsModerator(moderator))
                return;

            var target = BasePlayer.FindByID(targetId);
            var name = target != null ? target.displayName : targetId.ToString();

            CuiHelper.DestroyUi(moderator, UiRoot);
            var c = CreateModalRoot(moderator, "0.315 0.300", "0.685 0.700", out var window);
            AddLabel(c, window, "STEAM ID", 26, "0.08 0.765", "0.92 0.900", _config.Colors.Cream, TextAnchor.MiddleCenter, true);
            AddLabel(c, window, EscapeRichText(name), 14, "0.08 0.690", "0.92 0.760", _config.Colors.Lime, TextAnchor.MiddleCenter, false);
            AddLabel(c, window, "Нажмите на поле → Ctrl+A → Ctrl+C", 13, "0.08 0.575", "0.92 0.660", _config.Colors.Muted, TextAnchor.MiddleCenter, false);
            AddInputField(c, window, targetId.ToString(), 20, 24, true, string.Empty,
                "0.10 0.420", "0.90 0.555", TextAnchor.MiddleCenter, true);
            AddButton(c, window, "ВЕРНУТЬСЯ", _config.Colors.Lime,
                "0.10 0.160", "0.90 0.275", "glassreport.ui moderation 0", _config.Colors.ButtonText, 14);
            CuiHelper.AddUi(moderator, c);
        }

        private void ShowModerUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiRoot);

            var vanished = IsVanished(player);
            var c = CreateModalRoot(player, "0.300 0.235", "0.700 0.765", out var window);

            AddLabel(c, window, "ПАНЕЛЬ МОДЕРАТОРА", 25, "0.07 0.830", "0.93 0.940", _config.Colors.Cream, TextAnchor.MiddleCenter, true);
            AddLabel(c, window, EscapeRichText(TrimName(player.displayName, 32)), 15,
                "0.07 0.755", "0.93 0.825", "1.000 0.647 0.000 1.00", TextAnchor.MiddleCenter, false);
            AddLabel(c, window, vanished ? "ВАНИШ: ВКЛЮЧЁН" : "ВАНИШ: ВЫКЛЮЧЕН", 13,
                "0.07 0.690", "0.93 0.750", vanished ? _config.Colors.Lime : _config.Colors.Muted, TextAnchor.MiddleCenter, false);

            AddButton(c, window, vanished ? "ВЫКЛЮЧИТЬ ВАНИШ" : "ВКЛЮЧИТЬ ВАНИШ", _config.Colors.Lime,
                "0.10 0.535", "0.90 0.640", "glassreport.ui vanish", _config.Colors.ButtonText, 15);
            AddButton(c, window, "МЕНЮ РЕПОРТОВ И ПРОВЕРОК", _config.Colors.Lime,
                "0.10 0.390", "0.90 0.495", "glassreport.ui moderation 0", _config.Colors.ButtonText, 15);
            AddLabel(c, window, "Вызов на проверку: /check <ник|SteamID>", 11,
                "0.07 0.285", "0.93 0.350", _config.Colors.Muted, TextAnchor.MiddleCenter, false);
            AddButton(c, window, "ЗАКРЫТЬ", _config.Colors.Lime,
                "0.10 0.095", "0.90 0.185", "glassreport.ui close", _config.Colors.ButtonText, 13);

            CuiHelper.AddUi(player, c);
        }

        private CuiElementContainer CreateModalRoot(BasePlayer player, string min, string max, out string window)
        {
            var c = new CuiElementContainer();
            c.Add(new CuiPanel
            {
                Image = { Color = _config.Colors.Overlay },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true,
                KeyboardEnabled = true
            }, "Overlay", UiRoot);

            window = UiRoot + ".Modal";
            c.Add(new CuiPanel
            {
                Image = { Color = _config.Colors.Glass, Material = BlurMaterial, Sprite = RoundedSprite, ImageType = UnityEngine.UI.Image.Type.Sliced },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, UiRoot, window);

            c.Add(new CuiPanel
            {
                Image = { Color = _config.Colors.Lime, Sprite = RoundedSprite, ImageType = UnityEngine.UI.Image.Type.Sliced },
                RectTransform = { AnchorMin = "0.025 0.982", AnchorMax = "0.975 0.994" }
            }, window, window + ".Accent");
            return c;
        }

        private void AddPlayerAvatar(CuiElementContainer c, string parent, ulong steamId, string anchorMin, string anchorMax)
        {
            var frame = CuiHelper.GetGuid();
            c.Add(new CuiPanel
            {
                Image = { Color = "0.055 0.070 0.058 0.92", Sprite = RoundedSprite, ImageType = UnityEngine.UI.Image.Type.Sliced },
                RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax }
            }, parent, frame);

            c.Add(new CuiElement
            {
                Parent = frame,
                Components =
                {
                    new CuiRawImageComponent
                    {
                        SteamId = steamId.ToString(),
                        Color = "1 1 1 1",
                        FadeIn = 0.15f
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.035 0.035", AnchorMax = "0.965 0.965" }
                }
            });
        }

        private void AddInputField(CuiElementContainer c, string parent, string text, int fontSize, int charsLimit,
            bool readOnly, string command, string anchorMin, string anchorMax, TextAnchor align, bool autofocus)
        {
            var bg = CuiHelper.GetGuid();
            c.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.10 0.085 0.88", Sprite = RoundedSprite, ImageType = UnityEngine.UI.Image.Type.Sliced },
                RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax }
            }, parent, bg);

            c.Add(new CuiElement
            {
                Parent = bg,
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Text = text ?? string.Empty,
                        FontSize = fontSize,
                        Font = "RobotoCondensed-Regular.ttf",
                        Align = align,
                        Color = _config.Colors.Cream,
                        CharsLimit = charsLimit,
                        Command = command ?? string.Empty,
                        ReadOnly = readOnly,
                        NeedsKeyboard = true,
                        HudMenuInput = true,
                        Autofocus = autofocus,
                        LineType = UnityEngine.UI.InputField.LineType.SingleLine
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.025 0.08", AnchorMax = "0.975 0.92" },
                    new CuiNeedsCursorComponent(),
                    new CuiNeedsKeyboardComponent()
                }
            });
        }

        private void AddLabel(CuiElementContainer c, string parent, string text, int fontSize,
            string anchorMin, string anchorMax, string color, TextAnchor align, bool bold)
        {
            c.Add(new CuiLabel
            {
                Text =
                {
                    Text = bold ? $"<b>{text}</b>" : text,
                    FontSize = fontSize,
                    Align = align,
                    Color = color,
                    Font = "RobotoCondensed-Regular.ttf"
                },
                RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax }
            }, parent);
        }

        private void AddButton(CuiElementContainer c, string parent, string text, string background,
            string anchorMin, string anchorMax, string command, string textColor, int fontSize)
        {
            c.Add(new CuiButton
            {
                Button =
                {
                    Color = background,
                    NormalColor = background,
                    HighlightedColor = "0.760 0.930 0.620 1.00",
                    PressedColor = "0.590 0.790 0.430 1.00",
                    Command = command,
                    Sprite = RoundedSprite,
                    ImageType = UnityEngine.UI.Image.Type.Sliced
                },
                RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax },
                Text =
                {
                    Text = text,
                    FontSize = fontSize,
                    Align = TextAnchor.MiddleCenter,
                    Color = textColor,
                    Font = "RobotoCondensed-Regular.ttf"
                }
            }, parent);
        }

        #endregion

        #region Helpers

        private void BroadcastCheck(string message)
        {
            PrintToChat($"<color=#B0DF8E>[ПРОВЕРКА]</color> <color=#F6EDCF>{message}</color>");
        }

        private string GetArgTail(ConsoleSystem.Arg arg, int startIndex, int maxParts)
        {
            var parts = new List<string>();
            for (var i = startIndex; i < startIndex + maxParts; i++)
            {
                var value = arg.GetString(i, string.Empty);
                if (string.IsNullOrEmpty(value))
                    break;
                parts.Add(value);
            }
            return string.Join(" ", parts);
        }

        private bool IsValidBanDuration(string duration)
        {
            if (string.IsNullOrWhiteSpace(duration))
                return false;
            return Regex.IsMatch(duration, @"^(?:\d+[smhdwMy])+$", RegexOptions.CultureInvariant);
        }

        private string FormatDuration(string duration)
        {
            switch (duration)
            {
                case "1h": return "1 час";
                case "1d": return "1 день";
                case "7d": return "7 дней";
                case "30d": return "30 дней";
                default: return duration;
            }
        }

        private bool HasModeratorPermission(BasePlayer player)
        {
            return player != null && permission.UserHasPermission(player.UserIDString, PermissionModerator);
        }

        private bool IsVanished(BasePlayer player)
        {
            return player != null && _vanished.Contains(player.userID);
        }

        private void SetVanish(BasePlayer player, bool enable)
        {
            if (player == null)
                return;

            try
            {
                if (enable)
                {
                    if (Vanish != null && Vanish.IsLoaded)
                        Vanish.Call("Disappear", player);
                    else
                        BuiltinDisappear(player);

                    _vanished.Add(player.userID);
                    Reply(player, "Ванш <color=#B0DF8E>включён</color>. Вы невидимы для игроков.");
                }
                else
                {
                    if (Vanish != null && Vanish.IsLoaded)
                        Vanish.Call("Reappear", player);
                    else
                        BuiltinReappear(player);

                    _vanished.Remove(player.userID);
                    Reply(player, "Ванш <color=#E85D52>выключен</color>. Вы снова видимы.");
                }
            }
            catch (Exception ex)
            {
                PrintError($"Vanish error ({player.displayName}): {ex}");
            }
        }

        // Встроенный ванш (если плагин Vanish не установлен): скрываем сетевой объект
        // игрока от всех остальных подписчиков и отключаем коллайдер.
        private void BuiltinDisappear(BasePlayer player)
        {
            player.limitNetworking = true;
            player.DisablePlayerCollider();

            var connections = Net.sv.connections
                .Where(x => x.connected && x.isAuthenticated && x.player is BasePlayer && x.player != player)
                .ToList();
            player.OnNetworkSubscribersLeave(connections);
        }

        private void BuiltinReappear(BasePlayer player)
        {
            player.limitNetworking = false;
            player.EnablePlayerCollider();
            player.UpdateNetworkGroup();
            player.SendNetworkUpdateImmediate();
        }

        // Выдача/снятие права модератора по нику (онлайн) или SteamID (в т.ч. оффлайн).
        private string SetModerator(string query, bool give)
        {
            string id;
            string name;

            var online = FindOnlinePlayer(query);
            ulong parsed;
            if (online != null)
            {
                id = online.UserIDString;
                name = online.displayName;
            }
            else if (ulong.TryParse(query, out parsed) && query.Length >= 17)
            {
                id = query;
                var known = GetKnownPlayer(parsed);
                name = known != null && !string.IsNullOrWhiteSpace(known.Name) ? known.Name : query;
            }
            else
            {
                return "Игрок не найден (или несколько совпадений). Укажите точный ник или SteamID.";
            }

            if (give)
            {
                permission.GrantUserPermission(id, PermissionModerator, null);
                if (online != null)
                    Reply(online, $"Вам выдана модерка. Откройте панель: <color=#FFA500>/{_config.ModerCommand}</color>");
                return $"Игроку <color=#B0DF8E>{EscapeRichText(name)}</color> ({id}) выдана модерка.";
            }

            permission.RevokeUserPermission(id, PermissionModerator);
            if (online != null)
            {
                if (_vanished.Contains(online.userID))
                    SetVanish(online, false);
                CuiHelper.DestroyUi(online, UiRoot);
                CuiHelper.DestroyUi(online, UiModer);
                Reply(online, "Ваша модерка снята.");
            }
            return $"У игрока <color=#B0DF8E>{EscapeRichText(name)}</color> ({id}) снята модерка.";
        }

        private bool IsModerator(BasePlayer player)
        {
            if (player == null)
                return false;

            return player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionModerator);
        }

        private bool IsPlayerOnline(ulong userId)
        {
            var player = BasePlayer.FindByID(userId);
            return player != null && player.IsConnected;
        }

        private BasePlayer FindOnlinePlayer(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return null;

            ulong id;
            if (ulong.TryParse(query, out id))
            {
                var byId = BasePlayer.FindByID(id);
                if (byId != null && byId.IsConnected)
                    return byId;
            }

            var matches = BasePlayer.activePlayerList
                .Where(x => x != null && x.IsConnected && x.displayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            return matches.Count == 1 ? matches[0] : null;
        }

        private string F(float value)
        {
            return value.ToString("0.000", CultureInfo.InvariantCulture);
        }

        private string TrimName(string name, int maxLength)
        {
            if (string.IsNullOrEmpty(name))
                return "Неизвестно";

            return name.Length <= maxLength ? name : name.Substring(0, maxLength - 1) + "…";
        }

        private string EscapeRichText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value.Replace("<", "‹").Replace(">", "›");
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null)
                return;

            SendReply(player, $"<color=#B0DF8E>[ЖАЛОБЫ]</color> <color=#F6EDCF>{message}</color>");
        }

        #endregion
    }
}
