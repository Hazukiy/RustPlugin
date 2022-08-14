using System;
using Oxide;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Configuration;
using Oxide.Game.Rust.Cui;
using Rust;
using UnityEngine;
using Oxide.Core.Libraries.Covalence;
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("MyRustPlugin", "Luke", "1.0.0")]
    [Description("Extensions to the game")]
    public class MyRustPlugin : RustPlugin
    {
        private const string Blue = "#32a4f5";
        private const string Green = "#1cbf68";
        private const string Red = "#DE0F17";
        private string Prefix = $"<color={Green}>[Minge]</color> ";

        private const int ShowIcon = 0;
        private const int ReportTimeDelay = 300; // 600 = 10 minutes 
        
        
        private bool _isVoteDayActive;
        private int _totalVoteDayCount;
        private List<BasePlayer> _voteDayPlayers = new List<BasePlayer>();
        private Timer _voteDayTimeout;
        private Timer _voteDayCooldown;

        private PlayerData _playerData = new PlayerData();

        private void Init()
        {
            Server.Broadcast(GetFormattedMsg("Plugin loaded."), Prefix, ShowIcon);

            // Register the chat command
            cmd.AddChatCommand("time", this, nameof(TimeCommand));
            cmd.AddChatCommand("voteday", this, nameof(VoteDay));
        }

        #region Server Hooks
        private void OnServerInitialized()
        {
            // Load the player data file
            //_playerData = Interface.GetMod().DataFileSystem.ReadObject<PlayerData>(this.Title) ?? new PlayerData();

            timer.Every(ReportTimeDelay, () =>
            {
                Server.Broadcast($"Server time is now: {GetFormattedMsg($"{TOD_Sky.Instance.Cycle.DateTime:hh:mm} {TOD_Sky.Instance.Cycle.DateTime:tt}", Red)}", Prefix, ShowIcon);
            });
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            var welcome = GetFormattedMsg($"Commands: (/time, /voteday)");
            Player.Message(player, welcome, Prefix, ShowIcon);
            Server.Broadcast($"{GetFormattedMsg($"{player.displayName}", Green)} has connected.", Prefix, ShowIcon);
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            Server.Broadcast($"{GetFormattedMsg($"{player.displayName}", Red)} has disconnected.", Prefix, ShowIcon);
        }

        private object OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player.isClient)
            {
                Server.Broadcast(GetFormattedMsg($"{player.displayName} died like a noob."), Prefix, ShowIcon);
            }
            return null;
        }

        private object OnLootPlayer(BasePlayer player, BasePlayer target)
        {
            if (player.isClient)
            {
                Server.Broadcast(GetFormattedMsg($"{player.displayName} is looting {target.displayName}'s corpse."), Prefix, ShowIcon);
            }
            return null;
        }
        #endregion

        #region Commands

        private void TimeCommand(BasePlayer player, string command, string[] args)
        {
            Player.Message(player, $"Server time is now: {GetFormattedMsg($"{TOD_Sky.Instance.Cycle.DateTime:hh:mm} {TOD_Sky.Instance.Cycle.DateTime:tt}", Red)}", Prefix, ShowIcon);
        }

        private void VoteDay(BasePlayer player, string command, string[] args)
        {
            // Check if there's a cooldown
            if (_voteDayCooldown != null)
            {
                Player.Message(player, GetFormattedMsg($"Voteday command on cooldown."), Prefix, ShowIcon);
                return;
            }

            // If the player hasn't voted yet, add to the list of voted
            if (!_voteDayPlayers.Contains(player))
            {
                _voteDayPlayers.Add(player);
                _totalVoteDayCount++;
            }
            else
            {
                // You've already voted so return
                Player.Message(player, GetFormattedMsg($"You've already voted ya mingebag"), Prefix, ShowIcon);
                return;
            }

            // Make the nessessary calculations
            var totalPlayers = BasePlayer.activePlayerList.Count;
            var needed = Math.Round(totalPlayers * 0.6);

            // If there's not an active vote, start a timer
            if (!_isVoteDayActive)
            {
                _voteDayTimeout = timer.Once(60f, () =>
                {
                    Server.Broadcast(GetFormattedMsg($"Voteday timed out, only got {_totalVoteDayCount} / {needed} votes."), Prefix, ShowIcon);
                    _totalVoteDayCount = 0;
                    _isVoteDayActive = false;
                    _voteDayPlayers.Clear();
                });
            }

            // Set that there's an active vote 
            _isVoteDayActive = true;

            Server.Broadcast($"Player {GetFormattedMsg($"{player.displayName}", Green)} has voted to change time to day. {GetFormattedMsg($"({_totalVoteDayCount} / {needed} votes needed)", Red)}", Prefix, ShowIcon);

            // Check to see if vote needed has been met yet
            if (_totalVoteDayCount >= needed)
            {
                _totalVoteDayCount = 0;
                _isVoteDayActive = false;
                _voteDayPlayers.Clear();
                Server.Broadcast(GetFormattedMsg($"Vote successful, changing time to 9AM"), Prefix, ShowIcon);

                // Get rid of the timeout
                _voteDayTimeout.Destroy();

                // Set to 9AM
                TOD_Sky.Instance.Cycle.Hour = 9.0f;

                _voteDayCooldown = timer.Once(300f, () =>
                {
                    Puts("Voteday command can now be used.");

                    // Destroy the timer
                    _voteDayCooldown.Destroy();
                    _voteDayCooldown = null;
                });

                return;
            }
        }
        #endregion

        private string GetFormattedMsg(string msg, string colour = Blue) => $"<color={colour}>{msg}</color>";



        internal class PlayerData
        {
            public Dictionary<ulong, PlayerInfo> PlayerInfo = new Dictionary<ulong, PlayerInfo>();
            public PlayerData() { }
        }

        internal class PlayerInfo
        {
            public long TotalMinutes = 0;
        }
    }
}
