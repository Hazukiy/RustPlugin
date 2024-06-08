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
using Oxide.Game.Rust.Libraries;
using UnityEngine.UIElements;
using Rust.AI;
using Oxide.Ext;

namespace Oxide.Plugins
{
    [Info("ZombieMod", "Luke", "1.0.0")]
    [Description("Zombie mod for Rust")]
    class ZombieMod : RustPlugin
    {
        #region Fields
        private const string Blue = "#32a4f5";
        private const string Green = "#1cbf68";
        private const string Red = "#DE0F17";
        private string Prefix = $"<color={Green}>[ZM]</color> ";

        private const int ShowIcon = 0;
        private const int ReportTimeDelay = 300; // 600 = 10 minutes 

        private bool _isVoteDayActive;
        private int _totalVoteDayCount;
        private List<BasePlayer> _voteDayPlayers;
        private Timer _voteDayTimeout;
        private Timer _voteDayCooldown;
        #endregion

        private void Init()
        {
            _voteDayPlayers = new List<BasePlayer>();

            Server.Broadcast(MsgFmt("Zombie plugin loaded."), Prefix, ShowIcon);

            // Register the chat command
            cmd.AddChatCommand("time", this, nameof(TimeCommand));
            cmd.AddChatCommand("voteday", this, nameof(VoteDayCommand));

            // Zombie commands
            cmd.AddChatCommand("zspawn", this, nameof(SpawnZombieCommand));
            cmd.AddChatCommand("zclear", this, nameof(ClearZombiesCommand));
            cmd.AddChatCommand("zpop", this, nameof(GetZombieCountCommand));
            cmd.AddChatCommand("zre", this, nameof(RegenerateZombiesCommand));
        }

        #region Server Hooks
        private void OnServerInitialized()
        {
            // Initial map zombie spawning
            StartMapSpawning();

            // Broadcast the server time
            timer.Every(ReportTimeDelay, () =>
            {
                Server.Broadcast($"Server time is now: {MsgFmt($"{TOD_Sky.Instance.Cycle.DateTime:hh:mm} {TOD_Sky.Instance.Cycle.DateTime:tt}", Red)}", Prefix);
            });
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            Player.Message(player, MsgFmt($"Commands: (/time, /voteday)"), Prefix);
            Server.Broadcast($"{MsgFmt($"{player.displayName}", Green)} has connected.", Prefix);

            // TODO: Add radius check, X amount per sq of zombies to prevent over-spawning

            // Spawn zombies around player
            StartPlayerSpawning(player);
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            Server.Broadcast($"{MsgFmt($"{player.displayName}", Red)} has disconnected.", Prefix);
        }

        private object OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player.isClient)
            {
                Server.Broadcast(MsgFmt($"{player.displayName} died."), Prefix);
            }
            return null;
        }

        private object OnLootPlayer(BasePlayer player, BasePlayer target)
        {
            if (player.isClient)
            {
                Server.Broadcast(MsgFmt($"{player.displayName} is looting {target.displayName}'s corpse."), Prefix, ShowIcon);
            }
            return null;
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            //Puts($"Player {player.displayName} ({player.userID}) has spawned.");
            //timer.Every(1f, (() => RenderUI(player)));
        }
        #endregion

        #region Generic Commands
        private void TimeCommand(BasePlayer player, string command, string[] args)
        {
            Player.Message(player, $"Server time is now: {MsgFmt($"{TOD_Sky.Instance.Cycle.DateTime:hh:mm} {TOD_Sky.Instance.Cycle.DateTime:tt}", Red)}", Prefix, ShowIcon);
        }

        private void VoteDayCommand(BasePlayer player, string command, string[] args)
        {
            // Check if there's a cooldown
            if (_voteDayCooldown != null)
            {
                Player.Message(player, MsgFmt($"Voteday command on cooldown."), Prefix, ShowIcon);
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
                Player.Message(player, MsgFmt($"You've already voted ya mingebag"), Prefix, ShowIcon);
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
                    Server.Broadcast(MsgFmt($"Voteday timed out, only got {_totalVoteDayCount} / {needed} votes."), Prefix, ShowIcon);
                    _totalVoteDayCount = 0;
                    _isVoteDayActive = false;
                    _voteDayPlayers.Clear();
                });
            }

            // Set that there's an active vote 
            _isVoteDayActive = true;

            Server.Broadcast($"Player {MsgFmt($"{player.displayName}", Green)} has voted to change time to day. {MsgFmt($"({_totalVoteDayCount} / {needed} votes needed)", Red)}", Prefix, ShowIcon);

            // Check to see if vote needed has been met yet
            if (_totalVoteDayCount >= needed)
            {
                _totalVoteDayCount = 0;
                _isVoteDayActive = false;
                _voteDayPlayers.Clear();
                Server.Broadcast(MsgFmt($"Vote successful, changing time to 9AM"), Prefix, ShowIcon);

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

        #region Feature:Zombie Spawning
        private const bool EnableZombies = true;
        private const string ZombiePrefab = "assets/prefabs/npc/scarecrow/scarecrow.prefab";
        private const uint ZombiePrefabID = 3473349223;
        private const uint PlayerPrefabID = 4108440852;
        private const int GrenadeItemID = 1840822026;
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

        // Zombie radius options
        private const float MinPlayerSpawnDistance = 60.0f;
        private const float MaxPlayerSpawnDistance = 150.0f;

        // Spawn limits
        private const int MaxZombies = 5000;
        private const int MaxPerPlayer = 1;
        private int MaxPerInitMap = 500;

        // Zombie properties
        private const float ZombieHealth = 20.0f;

        // Globals
        private int _totalKilledZombies;

        public int TotalZombiesOnMap => GetEntitiesByPrefabId(ZombiePrefabID).Count;

        private void StartMapSpawning()
        {
            if (!EnableZombies) return;

            Server.Broadcast($"Starting zombie spawning...", Prefix);

            // Check the count from pref type
            var allZombies = GetEntitiesByPrefabId(ZombiePrefabID);
            if (allZombies.Count >= MaxPerInitMap)
            {
                // Skip rendering if there's already loads on the map
                Puts("Skipping map spawning as more than half are already spawned");
                return;
            }

            for (var i = 0; i < MaxPerInitMap; i++)
            {
                Vector3 pos = GetRandomPosition();
                SpawnZombie(pos);
            }

            Server.Broadcast($"Finished zombie spawning.", Prefix);
        }

        public void StartPlayerSpawning(BasePlayer player)
        {
            if (!EnableZombies) return;

            for (var i = 0; i < MaxPerPlayer; i++)
            {
                Vector3 pos = GetRandomPositionAroundPlayer(player);
                SpawnZombie(pos);
            }
        }

        public void SpawnZombie(Vector3 pos)
        {
            try
            {
                if (TotalZombiesOnMap >= MaxZombies)
                {
                    Puts("Zombie count reached");
                    return;
                }

                ScarecrowNPC zombie = GameManager.server.CreateEntity(ZombiePrefab, pos) as ScarecrowNPC;
                if (zombie == null)
                {
                    return;
                }

                zombie.Spawn();
                zombie.displayName = _zombieNames[Random.Range(0, _zombieNames.Count)];

                if (zombie.TryGetComponent(out BaseNavigator navigator))
                {
                    navigator.ForceToGround();
                    navigator.PlaceOnNavMesh(0);
                }

                // Set health
                zombie.SetMaxHealth(ZombieHealth);
                zombie.SetHealth(ZombieHealth);

                //Item.knife.butcherx1.694604

                ItemContainer inventory = zombie.inventory.containerBelt as ItemContainer;
                inventory.Clear();

                // TODO:
                // Add the knife
                //var knife = ItemManager.CreateByItemID(694604);
                //if (knife != null)
                //{
                //    inventory.itemList.Add(knife);
                //}

                //foreach (var item in inventory.itemList)
                //{
                //    Puts($"Inventory: {item}");
                //}

                Puts($"({TotalZombiesOnMap}) Zombie {zombie.displayName} at x={pos.x}, y={pos.y}, z={pos.z}");
            }
            catch (Exception ex)
            {
                Puts($"Exception when attempting SpawnZombie: {ex}");
                //throw;
            }
        }

        public void ClearAllZombies()
        {
            var allZombies = GetEntitiesByPrefabId(ZombiePrefabID);
            foreach (var zom in allZombies)
            {
                zom.Kill();
            }
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            //Puts($"Prefab name: {entity.name} | PrefabID: {entity.prefabID}");
            if (entity is ScarecrowNPC)
            {
                var target = entity as ScarecrowNPC;
                if (info != null && info.Initiator != null)
                {
                    BasePlayer player = info.Initiator as BasePlayer;
                    if (player == null || !player.IsConnected)
                    {
                        return;
                    }

                    _totalKilledZombies++;
                    Server.Broadcast($"{MsgFmt($"{player.displayName}", Green)} killed zombie {MsgFmt($"{target.displayName}", Red)}. {_totalKilledZombies} zombies have been killed, there are {TotalZombiesOnMap} left.", Prefix);
                }
            }
        }

        #region Commands
        private void SpawnZombieCommand(BasePlayer player, string command, string[] args)
        {
            StartPlayerSpawning(player);
        }

        private void ClearZombiesCommand(BasePlayer player, string command, string[] args)
        {
            ClearAllZombies();
            Player.Message(player, $"Zombies destroyed, population is now: {MsgFmt($"{TotalZombiesOnMap}", Red)}", Prefix, ShowIcon);
        }

        private void GetZombieCountCommand(BasePlayer player, string command, string[] args)
        {
            Player.Message(player, $"Current Zombie Population: {MsgFmt($"{TotalZombiesOnMap}", Red)}", Prefix, ShowIcon);
        }

        private void RegenerateZombiesCommand(BasePlayer player, string command, string[] args)
        {
            ClearAllZombies();
            StartMapSpawning();
            Player.Message(player, $"Current Zombie Population: {MsgFmt($"{TotalZombiesOnMap}", Red)}", Prefix, ShowIcon);
        }
        #endregion
        #endregion

        #region Feature:HUD(todo)
        private const string PanelName = "ZombieKillsUIPanel";

        private void RenderUI(BasePlayer player)
        {
            var elements = new CuiElementContainer();
            var panel = elements.Add(new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.7" }, // Background color (RGBA)
                RectTransform = { AnchorMin = "0.4 0.9", AnchorMax = "0.6 1.0" } // Position and size
            }, "Overlay", "ZombieKillsUIPanel");

            elements.Add(new CuiLabel
            {
                Text = { Text = $"Zombie Kills: {_totalKilledZombies}", FontSize = 18, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, panel);

            CuiHelper.AddUi(player, elements);
        }
        #endregion

        #region Helpers and Others
        private Vector3 GetRandomPositionAroundPlayer(BasePlayer player)
        {
            Vector3 playerPos = player.transform.position;
            Vector3 position = Vector3.zero;

            for (int i = 0; i < 6; i++)
            {
                position = new Vector3(Random.Range(playerPos.x - 20.0f, playerPos.x + 20.0f), 0, Random.Range(playerPos.z - 20.0f, playerPos.z + 20.0f));
                position.y = TerrainMeta.HeightMap.GetHeight(position);

                // If valid position
                if (!AntiHack.TestInsideTerrain(position) && !IsPosInObject(position) && !IsPosInOcean(position) && Vector3.Distance(playerPos, position) > 10.0f)
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
                if (!AntiHack.TestInsideTerrain(position) && !IsPosInObject(position) && !IsPosInOcean(position))
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

        private bool IsPosInObject(Vector3 position)
        {
            return Physics.OverlapSphere(position, 0.5f, SpawnLayerMask).Length > 0;
        }

        private bool IsPosInOcean(Vector3 position)
        {
            return WaterLevel.GetWaterDepth(position, true, false) > 0.25f;
        }

        private List<BaseEntity> GetEntitiesByPrefabId(uint prefabId)
        {
            List<BaseEntity> entities = new List<BaseEntity>();

            foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
            {
                BaseEntity entity = networkable as BaseEntity;
                if (entity != null && entity.prefabID == prefabId)
                {
                    entities.Add(entity);
                }
            }

            return entities;
        }

        private string MsgFmt(string msg, string colour = Blue) => $"<color={colour}>{msg}</color>";
        #endregion
    }
}
