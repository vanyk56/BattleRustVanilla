using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("PopCommand", "You", "1.0.0")]
    [Description("Команда !pop и /pop: показывает онлайн, сколько заходит и сколько спит")]
    class PopCommand : RustPlugin
    {
        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Pop"] = "<color=#ffd166>Население сервера</color>\n" +
                          "Онлайн: <color=#06d6a0>{0}</color>\n" +
                          "Заходят: <color=#4cc9f0>{1}</color>\n" +
                          "В очереди: <color=#ef476f>{2}</color>\n" +
                          "Спят: <color=#adb5bd>{3}</color>"
            }, this, "ru");

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Pop"] = "<color=#ffd166>Server population</color>\n" +
                          "Online: <color=#06d6a0>{0}</color>\n" +
                          "Joining: <color=#4cc9f0>{1}</color>\n" +
                          "Queued: <color=#ef476f>{2}</color>\n" +
                          "Sleeping: <color=#adb5bd>{3}</color>"
            }, this, "en");
        }

        #endregion

        #region Logic

        private void ShowPop(BasePlayer player)
        {
            int online = BasePlayer.activePlayerList.Count;
            int sleeping = BasePlayer.sleepingPlayerList.Count;
            int joining = ServerMgr.Instance.connectionQueue.Joining;
            int queued = ServerMgr.Instance.connectionQueue.Queued;

            string msg = lang.GetMessage("Pop", this, player.UserIDString);
            player.ChatMessage(string.Format(msg, online, joining, queued, sleeping));
        }

        #endregion

        #region Commands

        // /pop
        [ChatCommand("pop")]
        private void CmdPop(BasePlayer player, string command, string[] args)
        {
            ShowPop(player);
        }

        // !pop (обычное сообщение в чат)
        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (player == null || string.IsNullOrEmpty(message)) return null;

            if (message.Trim().ToLower() == "!pop")
            {
                ShowPop(player);
                return true; // не показывать "!pop" в общем чате
            }

            return null;
        }

        // консоль/RCON: pop
        [ConsoleCommand("pop")]
        private void CcmdPop(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null)
            {
                ShowPop(player);
                return;
            }

            arg.ReplyWith(
                $"Online: {BasePlayer.activePlayerList.Count}, " +
                $"Joining: {ServerMgr.Instance.connectionQueue.Joining}, " +
                $"Queued: {ServerMgr.Instance.connectionQueue.Queued}, " +
                $"Sleeping: {BasePlayer.sleepingPlayerList.Count}");
        }

        #endregion
    }
}
