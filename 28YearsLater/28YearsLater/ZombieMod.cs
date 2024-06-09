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
        private string Prefix = $"<color={Red}>[ZM]</color> ";

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
            cmd.AddChatCommand("zradius", this, nameof(GetZombieRadiusCountCommand));
        }

        #region Server Hooks
        private void OnServerInitialized()
        {
            // Initial map zombie spawning
            //StartMapSpawning(ZombieInitalAmount);

            // Broadcast the server time
            timer.Every(ReportTimeDelay, () =>
            {
                Server.Broadcast($"Server time is now: {MsgFmt($"{TOD_Sky.Instance.Cycle.DateTime:hh:mm} {TOD_Sky.Instance.Cycle.DateTime:tt}", Red)}", Prefix);
            });

            // Rebuild all players
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                BuildPlayer(player);
            }

            // Build map
            if (_zombieSpawnTimer == null)
            {
                _zombieSpawnTimer = timer.Every(120.0f, () => OnSpawnZombie());
            }
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            Player.Message(player, MsgFmt($"Commands: (/time, /voteday)"), Prefix);
            Server.Broadcast($"{MsgFmt($"{player.displayName}", Green)} has connected.", Prefix);

            BuildPlayer(player);
        }

        private void BuildPlayer(BasePlayer player)
        {
            //_zombiePlayerSpawnTimer = timer.Every(20.0f, () => OnCheckZombieRadius(player));

        }

        void OnUserConnected(IPlayer player)
        {
            Puts($"{player.Name} ({player.Id}) connected from {player.Address}");

            if (player.IsAdmin)
            {
                Puts($"{player.Name} ({player.Id}) is admin");
            }

            Puts($"{player.Name} is {(player.IsBanned ? "banned" : "not banned")}");

            Server.Broadcast($"Welcome {player.Name} to pain");
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            Server.Broadcast($"{MsgFmt($"{player.displayName}", Red)} has disconnected.", Prefix);
        }

        private object OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (info.Initiator is ScarecrowNPC)
            {
                Server.Broadcast($"{MsgFmt($"{player.displayName}")} {_deathReasons[Random.Range(0, _deathReasons.Count)]}", Prefix);
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
            if (player.IsConnected)
            {
                var zombiesInRadius = GetEntitiesWithinRadius(player.transform.position, ZombieSpawnRadius, ZombiePrefabID);
                if (zombiesInRadius.Count >= ZombieMaxPerRadius)
                {
                    return;
                }

                var amountToSpawn = ZombieMaxPerRadius - zombiesInRadius.Count;
                if (amountToSpawn > 0)
                {
                    StartPlayerSpawning(player, amountToSpawn);
                }
            }

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
        private readonly int SpawnLayerMask = LayerMask.GetMask("Default", "Tree", "Construction", "World", "Vehicle_Detailed", "Deployed");

        // Zombie feature setup
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
        private List<string> _zombieEnterPhase = new List<string>()
        {
            "An outbreak at an encampment caused {#} new zombies",
            "{#} zombies have entered the city",
            "A hospital has become overrun, creating {#} new zombies",
            "A camp of bandits have become overwhelmed, creating {#} new zombies",
            "{#} zombies emerged from the graveyard",
            "A quarantine breach has resulted in {#} new zombies",
            "A laboratory accident spawned {#} zombies",
            "{#} new zombies appeared from the forest",
            "A suburban neighborhood fell, adding {#} zombies",
            "{#} zombies rose after a failed military operation",
            "A shopping mall siege resulted in {#} zombies",
            "A school evacuation turned into chaos, making {#} new zombies",
            "{#} zombies emerged from a deserted highway",
            "An apartment complex outbreak created {#} zombies",
            "A research facility meltdown unleashed {#} zombies",
            "{#} zombies broke through the town's defenses",
            "A rural village succumbed, resulting in {#} zombies",
            "{#} new zombies came from an abandoned mine",
            "A cruise ship infestation led to {#} new zombies",
            "{#} zombies appeared after a prison riot"
        };

        // Zombie radius options
        private const float MinPlayerSpawnDistance = 90.0f;
        private const float MaxPlayerSpawnDistance = 200.0f;

        private Timer _zombieSpawnTimer;
        private Timer _zombiePlayerSpawnTimer;

        // Zombie Spawn limits
        private const int MaxZombies = 1500;
        private const int MaxPerPlayer = 5;
        private int ZombieInitalAmount = 50;
        private int ZombieRefreshAmount = 20;
        private int ZombieMinThreshold = 5;

        // Zombie Radius
        private float ZombieSpawnRadius = 100.0f;
        private int ZombieMaxPerRadius = 10;

        // Zombie properties
        private const float ZombieMinHealth = 50.0f;
        private const float ZombieMaxHealth = 150.0f;
        private int ZombieScrapCap = 20;

        // Globals
        private int _totalKilledZombies;

        public int TotalZombiesOnMap => GetEntitiesByPrefabId(ZombiePrefabID).Count;

        private void StartMapSpawning(int amount)
        {
            if (!EnableZombies) return;

            Puts($"Starting map zombie spawning...");

            if (TotalZombiesOnMap >= MaxZombies)
            {
                Puts("Max zombie population limit reached; skipping map spawning.");
                return;
            }

            for (var i = 0; i < amount; i++)
            {
                Vector3 pos = GetRandomPosition();
                SpawnZombie(pos);
            }
            Puts($"Finished map spawning {amount} zombies.");

            var phrase = _zombieEnterPhase[Random.Range(0, _zombieEnterPhase.Count)];
            phrase = phrase.Replace("{#}", $"{MsgFmt($"{amount}", Green)}");

            Server.Broadcast(phrase, Prefix);
        }

        public void StartPlayerSpawning(BasePlayer player, int amount)
        {
            if (!EnableZombies) return;

            for (var i = 0; i < amount; i++)
            {
                Vector3 pos = GetRandomPositionAroundPlayer(player);
                SpawnZombie(pos);
            }
        }

        //object OnConstructionPlace(BaseEntity entity, Construction component, Construction.Target constructionTarget, BasePlayer player)
        //{
        //    Puts("OnConstructionPlace works!");
        //    return true;
        //}

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
            var health = Random.Range(ZombieMinHealth, ZombieMaxHealth);
            zombie.SetMaxHealth(health);
            zombie.SetHealth(health);

            // Clear inventory
            ItemContainer inventory = zombie.inventory.containerBelt as ItemContainer;
            inventory.itemList.Clear();

            // Add weapon
            var itemDef = ItemManager.FindItemDefinition("knife.butcher");
            if (itemDef != null)
            {
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

        public void OnSpawnZombie()
        {
            StartMapSpawning(Random.Range(2, ZombieRefreshAmount));
        }

        public void OnCheckZombieRadius(BasePlayer player)
        {
            var randomRadius = Random.Range(ZombieSpawnRadius, 500.0f);
            var zombiesInRadius = GetEntitiesWithinRadius(player.transform.position, randomRadius, ZombiePrefabID);
            if (zombiesInRadius.Count >= ZombieMaxPerRadius)
            {
                return;
            }

            var amountToSpawn = Math.Abs(ZombieMaxPerRadius - zombiesInRadius.Count);
            Puts($"Spawning {amountToSpawn} zombies");

            if (amountToSpawn > 0)
            {
                StartPlayerSpawning(player, amountToSpawn);
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

                // Remove the corpse to avoid exploit
                corpse.Kill();
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
            StartPlayerSpawning(player, MaxPerPlayer);
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
            StartMapSpawning(ZombieInitalAmount);
            Player.Message(player, $"Current Zombie Population: {MsgFmt($"{TotalZombiesOnMap}", Red)}", Prefix, ShowIcon);
        }

        private void GetZombieRadiusCountCommand(BasePlayer player, string command, string[] args)
        {
            var radius = float.Parse(args[0]);
            var radiusCount = GetEntitiesWithinRadius(player.transform.position, radius, ZombiePrefabID);
            Player.Message(player, $"There are {MsgFmt($"{radiusCount.Count}", Red)} zombies with a {MsgFmt($"{radius}", Green)} radius of you", Prefix, ShowIcon);
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
        private List<BaseEntity> GetEntitiesWithinRadius(Vector3 position, float radius, uint prefabId)
        {
            List<BaseEntity> entities = new List<BaseEntity>();
            Vis.Entities(position, radius, entities);

            // Filter entities by prefabID
            return entities.Where(entity => entity.prefabID == prefabId).ToList();
        }

        private Vector3 GetRandomPositionAroundPlayer(BasePlayer player)
        {
            Vector3 playerPos = player.transform.position;
            Vector3 position = Vector3.zero;

            var randomX = Random.Range(ZombieSpawnRadius, 500.0f);
            var randomZ = Random.Range(ZombieSpawnRadius, 500.0f);

            for (int i = 0; i < 6; i++)
            {
                position = new Vector3(Random.Range(playerPos.x - randomX, playerPos.x + randomX), 0, Random.Range(playerPos.z - randomZ, playerPos.z + randomZ));
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
