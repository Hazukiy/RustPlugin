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
using Random = UnityEngine.Random;
using System.Collections;
using Physics = UnityEngine.Physics;
using ConVar;
using Pool = Facepunch.Pool;
using Vector3 = UnityEngine.Vector3;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Linq;
using ProtoBuf;

namespace Oxide.Plugins
{
    [Info("ZombieMod", "Luke", "1.0.0")]
    [Description("Zombie mod for Rust")]
    class ZombieMod : RustPlugin
    {
        private const string Blue = "#32a4f5";
        private const string Green = "#1cbf68";
        private const string Red = "#DE0F17";
        private string Prefix = $"<color={Green}>[ZombieMod]</color> ";

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
            Server.Broadcast(GetFormattedMsg("Zombie plugin loaded."), Prefix, ShowIcon);

            // Register the chat command
            cmd.AddChatCommand("time", this, nameof(TimeCommand));
            cmd.AddChatCommand("voteday", this, nameof(VoteDay));

            // Zombie commands
            cmd.AddChatCommand("zspawn", this, nameof(SpawnZombieCommand));
            cmd.AddChatCommand("zclear", this, nameof(DestroyZombies));
            cmd.AddChatCommand("zpop", this, nameof(GetZombieCount));
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

            // Initial some zombies

        }

        private void OnPlayerConnected(BasePlayer player)
        {
            var welcome = GetFormattedMsg($"Commands: (/time, /voteday)");
            Player.Message(player, welcome, Prefix, ShowIcon);
            Server.Broadcast($"{GetFormattedMsg($"{player.displayName}", Green)} has connected.", Prefix, ShowIcon);

            // Spawn zombies around player
            StartPlayerSpawning(player);
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

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            //Puts($"Prefab name: {entity.name} | PrefabID: {entity.prefabID}");

            switch (entity.prefabID)
            {
                case ZombiePrefabID:
                    if (info.InitiatorPlayer.isClient)
                    {
                        var attacker = info.InitiatorPlayer;
                        var zombie = entity as ScarecrowNPC;

                        _totalKilledZombies++;

                        _spawnedZombies.Remove(_spawnedZombies.FirstOrDefault(z => (z as BaseEntity).GetInstanceID() == entity.GetInstanceID()));
                        Server.Broadcast($"{GetFormattedMsg($"{attacker.displayName}", Green)} killed zombie {GetFormattedMsg($"{zombie.displayName}", Red)}. {_totalKilledZombies} zombies have been killed, there are {_spawnedZombies.Count} left.", Prefix, ShowIcon);
                    }
                    break;
                case PlayerPrefabID:
                    if (info.Initiator.prefabID == ZombiePrefabID)
                    {
                        var attacker = info.Initiator as ScarecrowNPC;
                        var player = entity.ToPlayer();

                        Server.Broadcast($"{GetFormattedMsg($"{player.displayName}", Red)} was killed by zombie {GetFormattedMsg($"{attacker.displayName}", Green)}.", Prefix, ShowIcon);
                    }
                    break;
            }

        }
        #endregion

        #region Generic Commands
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

        #region Zombie Spawning
        private const string ZombiePrefab = "assets/prefabs/npc/scarecrow/scarecrow.prefab";
        private const uint ZombiePrefabID = 3473349223;
        private const uint PlayerPrefabID = 4108440852;
        private readonly int SpawnLayerMask = LayerMask.GetMask("Default", "Tree", "Construction", "World", "Vehicle_Detailed", "Deployed");
        private List<string> _zombieNames = new List<string>
        {
            "Rotten Rick",
            "Decayed Dana",
            "Gory Greg",
            "Haunting Heather",
            "Undead Ulysses",
            "Fleshless Fiona",
            "Bony Bill",
            "Creepy Carla",
            "Vile Victor",
            "Mangled Max",
            "Putrid Patty",
            "Ghastly Gary",
            "Wicked Wanda",
            "Nefarious Ned",
            "Morbid Mary",
            "Lurking Larry",
            "Eerie Erica",
            "Shambling Sam",
            "Bloodless Betty",
            "Dreadful Doug",
            "Grotesque Gail",
            "Horrid Hank",
            "Decomposed Debbie",
            "Terrifying Tim",
            "Cadaverous Cathy",
            "Gruesome Glen",
            "Spooky Steve",
            "Macabre Molly",
            "Frantic Fred",
            "Abominable Alice",
            "Nightmarish Neil",
            "Rancid Rachel",
            "Livid Luke",
            "Murderous Marge",
            "Fatal Frank",
            "Twisted Tina",
            "Disturbing Dave",
            "Evil Edith",
            "Baleful Bob",
            "Ghoulie Grace",
            "Menacing Mike",
            "Jittery Jane",
            "Sinister Sid",
            "Haunted Hannah",
            "Corpse-like Carl",
            "Reeking Rhonda",
            "Shadowy Sean",
            "Frightening Faye",
            "Wretched Wayne",
            "Phantom Phil"
        };

        private const float MinSpawnDistance = 60.0f;
        private const float MaxSpawnDistance = 150.0f;

        private const int MaxZombies = 5000;
        private const int MaxPerPlayer = 1;

        // Zombie properties
        private const float ZombieHealth = 20.0f;

        // Cached zombies
        private List<ScarecrowNPC> _spawnedZombies = new List<ScarecrowNPC>();
        private int _totalKilledZombies;

        private void StartMapSpawning()
        {

        }

        public void StartPlayerSpawning(BasePlayer player)
        {
            for (var i = 0; i < MaxPerPlayer; i++)
            {
                SpawnZombie(player);
            }
        }

        public void SpawnZombie(BasePlayer player, bool usePlayer = true)
        {
            if (_spawnedZombies.Count >= MaxZombies)
            {
                Puts("Zombie count reached");
                return;
            }

            Vector3 position = usePlayer ? GetRandomPositionAroundPlayer(player) : GetRandomPosition();
            ScarecrowNPC zombie = GameManager.server.CreateEntity(ZombiePrefab, position) as ScarecrowNPC;
            if (!zombie)
            {
                return;
            }

            zombie.Spawn();
            zombie.displayName = GetRandomZombieName();
            if (zombie.TryGetComponent(out BaseNavigator navigator))
            {
                navigator.ForceToGround();
                navigator.PlaceOnNavMesh(0);
            }

            // Set health
            zombie.SetMaxHealth(ZombieHealth);
            zombie.SetHealth(ZombieHealth);

            // Add to cache
            _spawnedZombies.Add(zombie);

            var ent = zombie as BaseEntity;
            Player.Message(player, $"Zombie {zombie.displayName} ({ent.OwnerID}) spawned. Total of {_spawnedZombies.Count} spawned, {_totalKilledZombies} total killed zombies", Prefix, ShowIcon);
        }

        private string GetRandomZombieName() => _zombieNames[Random.Range(0, _zombieNames.Count)];

        private Vector3 GetRandomPositionAroundPlayer(BasePlayer player)
        {
            Vector3 playerPos = player.transform.position;
            Vector3 position = Vector3.zero;

            for (int i = 0; i < 6; i++)
            {
                position = new Vector3(Random.Range(playerPos.x - 20.0f, playerPos.x + 20.0f), 0, Random.Range(playerPos.z - 20.0f, playerPos.z + 20.0f));
                position.y = TerrainMeta.HeightMap.GetHeight(position);

                // If valid position
                if (!AntiHack.TestInsideTerrain(position) && !IsInObject(position) && !IsInOcean(position) && Vector3.Distance(playerPos, position) > 10.0f)
                {
                    break;
                }
            }
            return position;
        }

        private Vector3 GetRandomPosition()
        {
            Vector3 position = Vector3.zero;

            for (int i = 0; i < 6; i++)
            {
                float x = Random.Range(-TerrainMeta.Size.x / 2, TerrainMeta.Size.x / 2),
                      z = Random.Range(-TerrainMeta.Size.z / 2, TerrainMeta.Size.z / 2),
                      y = TerrainMeta.HeightMap.GetHeight(new Vector3(x, 0, z));

                position = new Vector3(x, y + 0.5f, z);

                // If valid position
                if (!AntiHack.TestInsideTerrain(position) && !IsInObject(position) && !IsInOcean(position))
                {
                    break;
                }
            }

            if (position == Vector3.zero)
            {
                position.y = TerrainMeta.HeightMap.GetHeight(0, 0);
            }

            return position;
        }

        private bool IsInObject(Vector3 position)
        {
            return Physics.OverlapSphere(position, 0.5f, SpawnLayerMask).Length > 0;
        }

        private bool IsInOcean(Vector3 position)
        {
            return WaterLevel.GetWaterDepth(position, true, false) > 0.25f;
        }

        private void SpawnZombieCommand(BasePlayer player, string command, string[] args)
        {
            StartPlayerSpawning(player);
        }

        private void DestroyZombies(BasePlayer player, string command, string[] args)
        {
            // TODO: Get all scarecrow on map and wipe them out

            foreach (ScarecrowNPC zom in _spawnedZombies)
            {
                zom.Kill();
            }
            _spawnedZombies.Clear();

            Player.Message(player, $"Zombies destroyed, population is now: {GetFormattedMsg($"{_spawnedZombies.Count}", Red)}", Prefix, ShowIcon);
        }

        private void GetZombieCount(BasePlayer player, string command, string[] args)
        {
            Player.Message(player, $"Current Zombie Population: {GetFormattedMsg($"{_spawnedZombies.Count}", Red)}", Prefix, ShowIcon);
        }
        #endregion

        #region Other

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

        #endregion
    }
}
