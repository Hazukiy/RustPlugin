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
using Oxide.Core.Libraries;

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
            if (info.Initiator is ScarecrowNPC)
            {
                Server.Broadcast(MsgFmt($"{player.displayName} {_deathReasons[Random.Range(0, _deathReasons.Count)]}"), Prefix);
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
        private List<string> _zombieWeapons = new List<string>()
        {
            "knife.butcher"
        };
        
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

        private List<string> _deathReasons = new List<string>()
        {
            "Tried to give a zombie a high five and became the appetizer",
            "Was voted 'Most Delicious' by a group of zombies",
            "Challenged a zombie to a dance-off and lost... and got eaten",
            "Mistook a zombie for a friendly hugger",
            "Asked a zombie for directions and got eaten instead of getting an answer",
            "Tried to use a zombie as a piñata at a birthday party",
            "Got too close to a zombie while trying to take a selfie",
            "Played 'Ring Around the Rosie' with zombies and forgot the 'fall down' part",
            "Was convinced that zombies were just misunderstood and got a zombie hug",
            "Tried to teach zombies how to do the macarena and got eaten in the process",
            "Thought zombies were just looking for a friend and volunteered to be one",
            "Challenged a zombie to a staring contest and blinked first... and got eaten",
            "Tried to outsmart zombies by playing dead... and ended up being dinner",
            "Suggested a game of 'Duck, Duck, Goose' with zombies and regretted it",
            "Invited zombies to a tea party and ended up being the main course",
            "Attempted to lead a conga line with zombies and became the leader... of the buffet",
            "Thought a zombie was just a really dedicated cosplayer and tried to take a photo",
            "Offered a zombie a handshake and ended up armless",
            "Asked a zombie for a bite of their sandwich and got more than they bargained for",
            "Tried to make friends with zombies and ended up being their snack",
            "Suggested a game of 'Red Rover' with zombies and got sent straight to the afterlife",
            "Thought zombies were just trying to give a massage and ended up on the menu",
            "Offered to be the zombie's tour guide and ended up as their tour snack",
            "Mistook a zombie for a helpful hand and got a mouthful of fingers",
            "Tried to give a zombie a makeover and ended up being the makeover",
            "Tried to impress zombies with their dance moves and ended up being the main attraction",
            "Thought zombies were just really dedicated fans of 'Thriller' and joined the dance",
            "Asked a zombie for a bite of their burger and ended up as the burger",
            "Challenged a zombie to a staring contest and lost... and got eaten",
            "Thought zombies were just playing tag and became 'it' forever",
            "Tried to tell jokes to zombies and became the punchline",
            "Thought zombies were just looking for a game of 'Simon Says' and joined in",
            "Tried to convince zombies to join a game of 'Hide and Seek' and got found",
            "Thought zombies were just trying to give a hug and ended up as the hug",
            "Offered zombies a piece of candy and ended up as their treat",
            "Tried to show off their karaoke skills to zombies and got booed off stage... and eaten",
            "Thought zombies were just looking for a hug and offered one",
            "Mistook a zombie for a friendly neighbor and invited them in for tea... and brains",
            "Tried to teach zombies how to do the cha-cha and got chomped instead",
            "Thought zombies were just trying to play 'Tag' and joined the game",
            "Asked a zombie for a bite of their sandwich and got more than they bargained for",
            "Tried to impress zombies with their dance moves and ended up being the main attraction",
            "Thought zombies were just really dedicated fans of 'Thriller' and joined the dance",
            "Asked a zombie for a bite of their burger and ended up as the burger",
            "Challenged a zombie to a staring contest and lost... and got eaten",
            "Thought zombies were just playing tag and became 'it' forever",
            "Tried to tell jokes to zombies and became the punchline",
            "Thought zombies were just looking for a game of 'Simon Says' and joined in",
            "Tried to convince zombies to join a game of 'Hide and Seek' and got found",
            "Thought zombies were just trying to give a hug and ended up as the hug",
            "Offered zombies a piece of candy and ended up as their treat",
            "Tried to show off their karaoke skills to zombies and got booed off stage... and eaten",
            "Thought zombies were just looking for a hug and offered one",
            "Mistook a zombie for a friendly neighbor and invited them in for tea... and brains",
            "Tried to teach zombies how to do the cha-cha and got chomped instead",
            "Thought zombies were just trying to play 'Tag' and joined the game",
        };

        // Zombie radius options
        private const float MinPlayerSpawnDistance = 60.0f;
        private const float MaxPlayerSpawnDistance = 150.0f;

        // Spawn limits
        private const int MaxZombies = 5000;
        private const int MaxPerPlayer = 1;
        private int MaxPerInitMap = 50;

        // Zombie properties
        private const float ZombieHealth = 20.0f;
        private int ZombieScrapCap = 50;

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

            // Clear inventory
            ItemContainer inventory = zombie.inventory.containerBelt as ItemContainer;
            inventory.itemList.Clear();

            // Add weapon
            var itemDef = ItemManager.FindItemDefinition("knife.butcher");
            if (itemDef != null)
            {
                //inventory.itemList.Add(knife);
                var zomObj = zombie as BaseCombatEntity;
                Item weaponItem = ItemManager.CreateByItemID(itemDef.itemid, 1, 0);
                zomObj.GiveItem(weaponItem, BaseEntity.GiveItemReason.Generic);
            }

            Puts($"({TotalZombiesOnMap}) Zombie {zombie.displayName} at x={pos.x}, y={pos.y}, z={pos.z}");
        }

        public void ClearAllZombies()
        {
            var allZombies = GetEntitiesByPrefabId(ZombiePrefabID);
            foreach (var zom in allZombies)
            {
                zom.Kill();
            }
        }

        BaseCorpse OnCorpsePopulate(BasePlayer npcPlayer, NPCPlayerCorpse corpse)
        {
            if (npcPlayer is ScarecrowNPC)
            {
                foreach (var item in corpse.containers)
                {
                    // Scrap
                    var scrap = ItemManager.FindItemDefinition("scrap");
                    if (scrap != null)
                    {
                        item.AddItem(scrap, Random.Range(0, ZombieScrapCap));
                    }

                    // Low chance of blood
                    var chance = Random.Range(0, 10);
                    if (chance == 5)
                    {
                        var blood = ItemManager.FindItemDefinition("blood");
                        if (blood != null)
                        {
                            item.AddItem(blood, 1);
                        }
                    }

                    // Fragments
                    var fragments = ItemManager.FindItemDefinition("bone.fragments");
                    if (fragments != null)
                    {
                        item.AddItem(fragments, Random.Range(0, 5));
                    }
                }
                return corpse;
            }

            return null;
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
