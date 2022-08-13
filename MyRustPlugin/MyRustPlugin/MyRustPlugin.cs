using System;
using Oxide;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Configuration;
using Oxide.Game.Rust.Cui;
using Rust;
using UnityEngine;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("MyRustPlugin", "Luke", "1.0.0")]
    [Description("Extensions to the game")]
    public class MyRustPlugin : RustPlugin
    {
        private const string Prefix = "<color=#CA3333>[MingePlugin]</color>: ";
        private const int ShowIcon = 0;
        private const int Time = 300; // 600 = 10 minutes
        private PluginConfig _config;

        private void Init()
        {
            _config = Config.ReadObject<PluginConfig>();

            Server.Broadcast("MingePlugin loaded.", Prefix);

            // Register the chat command
            cmd.AddChatCommand("time", this, nameof(TimeCommand));
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(GetDefaultConfig(), true);
        }

        private PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                ShowJoinMessage = true,
                ShowLeaveMessage = true,
                JoinMessage = "Welcome you cunt",
                LeaveMessage = "Goodbye, cunt."
            };
        }

        private void OnServerInitialized()
        {
            timer.Every(Time, () =>
            {
                var time = $"Server time - {TOD_Sky.Instance.Cycle.DateTime:hh:mm} {TOD_Sky.Instance.Cycle.DateTime:tt}";
                Server.Broadcast(time, Prefix, ShowIcon);
                Puts(time);
            });
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            var welcome = $"Do /time to get current time.";
            Player.Message(player, welcome, Prefix, ShowIcon);
        }

        private void TimeCommand(BasePlayer player, string command, string[] args)
        {
            var time = $"Server time - {TOD_Sky.Instance.Cycle.DateTime:hh:mm} {TOD_Sky.Instance.Cycle.DateTime:tt}";
            Player.Message(player, time, Prefix, ShowIcon);
        }
    }

    public class PluginConfig
    {
        public bool ShowJoinMessage { get; set; }
        public bool ShowLeaveMessage { get; set; }
        public string JoinMessage { get; set; }
        public string LeaveMessage { get; set; }
    }
}
