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
using CompanionServer.Handlers;
using static ConsoleSystem;
using Steamworks.ServerList;
using System.Text;

namespace Oxide.Plugins
{
    [Info("ZombieMod", "Luke", "1.0.1")]
    [Description("Zombie mod for Rust")]
    class ZombieMod : RustPlugin
    {
        #region Core
        private const string Version = "1.0.1";
        private const string Blue = "#011575";
        private const string Green = "#1cbf68";

        private const string PrimaryRed = "#DF3434";
        private const string SecondaryRed = "#BD2323";

        private const string Orange = "#ED7A00";
        private const string Yellow = "#DFEC08";
        private const string Prefix = "[ZM]";
        private string ChatPrefix = $"<color={SecondaryRed}>{Prefix}</color>";

        // The delay between general messages
        private const int ReportTimeDelay = 700;

        // Voteday related
        private bool _isVoteDayActive;
        private int _totalVoteDayCount;
        private List<BasePlayer> _voteDayPlayers;
        private Timer _voteDayTimeout;
        private Timer _voteDayCooldown;

        // Cache
        private Dictionary<ulong, bool> godModePlayers;
        private List<PlayerStats> _connectedPlayers;
        private Hash<ulong, bool> noclipPlayers;

        private void Init()
        {
            godModePlayers = new Dictionary<ulong, bool>();
            noclipPlayers = new Hash<ulong, bool>();
            _connectedPlayers = new List<PlayerStats>();
            _voteDayPlayers = new List<BasePlayer>();

            // Register general commands
            cmd.AddChatCommand("time", this, nameof(TimeCommand));
            cmd.AddChatCommand("voteday", this, nameof(VoteDayCommand));

            // Admin
            cmd.AddChatCommand("godmode", this, nameof(ToggleGodModeCommand));
            cmd.AddChatCommand("heal", this, nameof(HealCommand));
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyPlayerUI(player);
            }

            _playerUIs.Clear();
            _connectedPlayers.Clear();
            _voteDayPlayers.Clear();
            godModePlayers.Clear();

            _voteDayTimeout = null;
            _voteDayCooldown = null;
            _connectedPlayers = null;
            _zombieSpawnTimer = null;
            godModePlayers = null;
        }

        public void BuildPlayer(BasePlayer player)
        {
            var playerData = _connectedPlayers.FirstOrDefault(p => p.Owner == player);
            if (playerData == null)
            {
                var stats = new PlayerStats()
                {
                    Owner = player
                };
                stats.BaseStats.JoinedTime = DateTime.Now;
                _connectedPlayers.Add(stats);
            }

            // Generate UI
            RegenerateUI(player);

            // Start zombie scan timer
            if (!_playerScanTimer.TryGetValue(player, out Timer scanTimer))
            {
                scanTimer = timer.Every(ScanTimerInterval, () => OnCheckZombieRadius(player));

                // Add
                _playerScanTimer.Add(player, scanTimer);
            }
        }

        private void DestroyPlayer(BasePlayer player)
        {
            // Remove stats
            _connectedPlayers.Remove(_connectedPlayers.FirstOrDefault(p => p.Owner == player));

            // Remove zombie spawn timer
            if (_playerScanTimer.TryGetValue(player, out Timer playerTimer))
            {
                playerTimer.Destroy();
                _playerScanTimer.Remove(player);
            }

            SendConsoleMessage($"Cleaned up player {player.displayName} after disconnect.");
        }

        #region General Hooks & Events
        private void OnServerInitialized()
        {
            // Start timer for broadcasting server time
            timer.Every(ReportTimeDelay, () =>
            {
                SendServerAnnouncement($"Server time is now: {Format($"{TOD_Sky.Instance.Cycle.DateTime:hh:mm} {TOD_Sky.Instance.Cycle.DateTime:tt}", PrimaryRed)}");
            });

            // Get zombies
            var zombieCount = GetEntitiesByPrefabId(ZombiePrefabID).Count;
            SendServerAnnouncement($"ZombieMod {Format($"v{Version}", Green)} successfully loaded. {zombieCount} zombies currently on map.");
            SendConsoleMessage($"ZombieMod v{Version} successfully loaded.");

            // Initialise zombies
            InitZombies();

            // Rebuild each player
            foreach (var player in BasePlayer.activePlayerList)
            {
                BuildPlayer(player);
            }
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            SendPlayerMessage(player, "Commands: (/time, /voteday)");
            SendServerAnnouncement($"{Format($"{player.displayName}", Green)} has connected.");

            BuildPlayer(player);

            // Regenerate UI
            // Rebuild each player
            ReloadAllPlayerUI();
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            SendServerAnnouncement($"{Format($"{player.displayName}", PrimaryRed)} has disconnected.");

            DestroyPlayer(player);

            // Regenerate UI
            // Rebuild each player
            ReloadAllPlayerUI();
        }

        private void ReloadAllPlayerUI()
        {
            // Get online players only
            var online = BasePlayer.activePlayerList.Where(p => p.IsConnected);
            if (online != null)
            {
                foreach (var player in online)
                {
                    RegenerateUI(player);
                }
            }
        }

        private object OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (info?.Initiator != null && info?.Initiator is ScarecrowNPC)
            {
                SendServerAnnouncement($"{Format(player.displayName, PrimaryRed)} {_deathReasons[Random.Range(0, _deathReasons.Count)]}");

                if (player.IsConnected)
                {
                    IncreasePlayerDeaths(player);
                }
            }
            return null;
        }

        private object OnLootPlayer(BasePlayer player, BasePlayer target)
        {
            if (player.IsConnected)
            {
                SendServerAnnouncement($"{Format($"{player.displayName}", PrimaryRed)} is looting {Format($"{target.displayName}", Green)}'s corpse.");
            }
            return null;
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player.IsConnected)
            {
                BuildPlayer(player);

                // Initial zombies 
                OnCheckZombieRadius(player);
            }
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            BasePlayer player = entity as BasePlayer;

            // Check godmode
            if (player != null && player.IsConnected)
            {
                if (godModePlayers.ContainsKey(player.userID) && godModePlayers[player.userID])
                {
                    info.damageTypes = new DamageTypeList();
                    info.HitMaterial = 0;
                    info.PointStart = Vector3.zero;
                    info.PointEnd = Vector3.zero;
                }
                else
                {
                    if (info.Initiator is ScarecrowNPC)
                    {
                        // Apply the damage multiplier
                        info.damageTypes.ScaleAll(DamageMultiplier);
                    }
                }
            }
        }
        #endregion

        #region General Commands
        private void ToggleGodModeCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (!player.IsAdmin)
            {
                SendPlayerMessage(player, "Nope :/");
                return;
            }

            if (godModePlayers.ContainsKey(player.userID))
            {
                godModePlayers[player.userID] = !godModePlayers[player.userID];
            }
            else
            {
                godModePlayers[player.userID] = true;
            }

            if (godModePlayers[player.userID])
            {
                player.ChatMessage("God mode enabled.");
            }
            else
            {
                player.ChatMessage("God mode disabled.");
            }
        }

        private void HealCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (!player.IsAdmin)
            {
                SendPlayerMessage(player, "Nope :/");
                return;
            }

            player.health = player.MaxHealth();

            SendPlayerMessage(player, $"{Format("Healed to full", Green)}");
        }

        private void TimeCommand(BasePlayer player, string command, string[] args)
        {
            SendPlayerMessage(player, $"Server time is now: {Format($"{TOD_Sky.Instance.Cycle.DateTime:hh:mm} {TOD_Sky.Instance.Cycle.DateTime:tt}", Green)}");
        }

        private void VoteDayCommand(BasePlayer player, string command, string[] args)
        {
            // Check if there's a cooldown
            if (_voteDayCooldown != null)
            {
                SendPlayerMessage(player, $"Voteday command on cooldown");
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
                SendPlayerMessage(player, $"You have already voted");
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
                    SendServerAnnouncement($"Voteday timed out, only got {Format($"{_totalVoteDayCount} / {needed}", PrimaryRed)} votes.");
                    _totalVoteDayCount = 0;
                    _isVoteDayActive = false;
                    _voteDayPlayers.Clear();
                });
            }

            // Set that there's an active vote 
            _isVoteDayActive = true;

            SendServerAnnouncement($"Player {Format(player.displayName, Green)} has voted to change time to day. {Format($"({_totalVoteDayCount} / {needed} votes needed)", PrimaryRed)}");

            // Check to see if vote needed has been met yet
            if (_totalVoteDayCount >= needed)
            {
                _totalVoteDayCount = 0;
                _isVoteDayActive = false;
                _voteDayPlayers.Clear();

                SendServerAnnouncement($"Voteday command successful, changing to {Format("9am", Green)}");

                // Get rid of the timeout
                _voteDayTimeout.Destroy();

                // Set to 9AM
                TOD_Sky.Instance.Cycle.Hour = 9.0f;

                _voteDayCooldown = timer.Once(300f, () =>
                {
                    SendConsoleMessage("Voteday command can now be used.");

                    // Destroy the timer
                    _voteDayCooldown.Destroy();
                    _voteDayCooldown = null;
                });

                return;
            }
        }
        #endregion
        #endregion

        #region Feature:Zombies
        private const string ZombiePrefab = "assets/prefabs/npc/scarecrow/scarecrow.prefab";
        private const string ZombieCorpsePrefab = "assets/prefabs/npc/murderer/murderer_corpse.prefab";
        private const bool EnableZombies = true; // False turns zombie spawning off
        private const uint ZombiePrefabID = 3473349223;

        // Zombie feature setup
        private readonly int SpawnLayerMask = LayerMask.GetMask("Default", "Tree", "Construction", "World", "Vehicle_Detailed", "Deployed");
        private readonly List<string> _zombieWeapons = new List<string>()
        {
            "knife.butcher",
            "knife.bone",
            "knife.combat"
        };  
        private readonly List<string> _zombieNames = new List<string>
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
        private readonly List<string> _deathReasons = new List<string>()
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
        private readonly List<string> _zombieEnterPhase = new List<string>()
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
        private readonly List<string> _scarySpawnMessages = new List<string>()
        {
            "You hear rustling in some bushes nearby.",
            "Something is watching you from the shadows...",
            "A shadowy figure moves in the corner of your eye...",
            "You feel an eerie chill run down your spine.",
            "Whispers echo softly in the darkness around you.",
            "You sense an unseen presence lingering nearby...",
            "The air grows heavy with an unsettling silence.",
            "The hairs on the back of your neck stand on end.",
            "A cold shiver crawls up your back unexpectedly.",
            "The night seems darker, enveloping you in its embrace.",
            "An ominous feeling settles over the area."
        };
        private readonly List<ZombieLootTableItem> _zombieLootTable = new List<ZombieLootTableItem>()
        {
            new ZombieLootTableItem() { Index = 0, Name = "scrap", MinAmount = 5, MaxAmount = 10 },
            new ZombieLootTableItem() { Index = 1, Name = "bone.fragments", MinAmount = 5, MaxAmount = 10 },
        };
        private static Dictionary<string, string> _zombiePlayerTitles = new Dictionary<string, string>()
        {
            { "0-9", "Zombie Bait" },
            { "10-19", "Novice Survivor" },
            { "20-29", "Grizzled Fighter" },
            { "30-39", "Undead Slayer" },
            { "40-49", "Zombie Hunter" },
            { "50-59", "Apocalypse Survivor" },
            { "60-69", "Zombie Annihilator" },
            { "70-79", "Ultimate Undead Slayer" },
            { "80-89", "Zombie Exterminator" },
            { "90-99", "Legendary Apocalypse Survivor" },
            { "100+", "Master of the Undead" }
        };

        private const float DamageMultiplier = 0.3f; // How much damage a zombie does to the player
        private Timer _zombieSpawnTimer;
        private Dictionary<BasePlayer, Timer> _playerScanTimer;

        #region Zombie Properties
        // Zombie Map Spawn limits
        private const int MaxZombies = 1500;
        private const int MinPerRegen = 5; // Min amount of zombies to respawn around map
        private const int MaxPerRegen = 20; // How many zombies can be spawned on the map at a single time
        private float ZombieSpawnTime = (float)TimeSpan.FromMinutes(15).TotalSeconds; // How often to spawn zombies on the map 

        // Base Zombie Radius
        private const float MinSpawnRadius = 100.0f;
        private const float MaxSpawnRadius = 300.0f;

        // Zombie properties
        private const float MinZombieHealth = 50.0f;
        private const float MaxZombieHealth = 130.0f;
        private const float MinEasyZombieHealth = 20.0f;
        private const float MaxEasyZombieHealth = 40.0f;
        private const int MaxScrapLoot = 5;

        // Zombie scan
        private float ScanTimerInterval = (float)TimeSpan.FromMinutes(10).TotalSeconds;
        private const float MinScanSpawnRadius = 60.0f;
        private const float MaxScanSpawnRadius = 100.0f;
        private const int MinScanSpawnAmount = 2;
        private const int MaxScanSpawnAmount = 10;
        private const int MaxScanInRadius = 10;
        #endregion

        public void InitZombies()
        {
            _playerScanTimer = new Dictionary<BasePlayer, Timer>();

            // Chat commands
            cmd.AddChatCommand("zspawn", this, nameof(SpawnZombieCommand));
            cmd.AddChatCommand("zclear", this, nameof(ClearZombiesCommand));
            cmd.AddChatCommand("zpop", this, nameof(GetZombieCountCommand));
            cmd.AddChatCommand("zre", this, nameof(RegenerateZombiesCommand));
            cmd.AddChatCommand("zradius", this, nameof(GetZombieRadiusCountCommand));

            // Get current count and spawn some initial zombies
            var mapCount = GetEntitiesByPrefabId(ZombiePrefabID).Count;
            if (mapCount <= (MaxZombies/2))
            {
                StartMapSpawning(MaxPerRegen);
            }

            // Now kick off a timer for spawning many zombies
            _zombieSpawnTimer = timer.Every(ZombieSpawnTime, () =>
            {
                StartMapSpawning(Random.Range(MinPerRegen, MaxPerRegen));
            });
        }

        #region Spawning
        private void StartMapSpawning(int amount)
        {
            if (!EnableZombies) return;

            SendConsoleMessage($"(MapSpawning) Spawning {amount} zombies...");
            var totalOnMap = GetEntitiesByPrefabId(ZombiePrefabID).Count;
            if (totalOnMap >= MaxZombies)
            {
                SendConsoleMessage($"(MapSpawning) Zombie limit of {MaxZombies} reached with a total of {totalOnMap}.");
                return;
            }

            // Simply get a random pos and spawn the zombie
            for (var i = 0; i < amount; i++)
            {
                Vector3 pos = GetRandomPositionOnMap();
                var health = Random.Range(MinZombieHealth, MaxZombieHealth); 
                SpawnZombie(pos, health);
            }
            SendConsoleMessage($"(MapSpawning) Finished spawning {amount} zombies.");

            // In-game notification of the event, guess to make it more RP friendly 
            var phrase = _zombieEnterPhase[Random.Range(0, _zombieEnterPhase.Count)];
            phrase = phrase.Replace("{#}", $"{Format(amount, Green)}");

            SendServerAnnouncement(phrase);
        }

        public void StartPlayerSpawning(BasePlayer player, int amount, float minRadius, float maxRadius)
        {
            if (!EnableZombies) return;

            SendConsoleMessage($"(PlayerSpawning) Spawning {amount} zombies...");
            var totalOnMap = GetEntitiesByPrefabId(ZombiePrefabID).Count;
            if (totalOnMap >= MaxZombies)
            {
                SendConsoleMessage($"(PlayerSpawning) Zombie limit of {MaxZombies} reached with a total of {totalOnMap}.");
                return;
            }

            // Spawn
            for (var i = 0; i < amount; i++)
            {
                Vector3 pos = GetRandomPositionAroundPlayer(player, minRadius, maxRadius);
                var health = Random.Range(MinEasyZombieHealth, MaxEasyZombieHealth);

                SpawnZombie(pos, health);
            }
            SendConsoleMessage($"(PlayerSpawning) Finished spawning {amount} zombies.");

            var phrase = _scarySpawnMessages[Random.Range(0, _scarySpawnMessages.Count)];
            SendPlayerMessage(player, phrase);
        }

        public void SpawnZombie(Vector3 pos, float health)
        {
            ScarecrowNPC zombie = GameManager.server.CreateEntity(ZombiePrefab, pos) as ScarecrowNPC;
            if (zombie == null)
            {
                return;
            }

            var zombieEnt = zombie as BaseCombatEntity;

            zombie.Spawn();
            zombie.displayName = _zombieNames[Random.Range(0, _zombieNames.Count)];

            if (zombie.TryGetComponent(out BaseNavigator navigator))
            {
                navigator.ForceToGround();
                navigator.PlaceOnNavMesh(0);
            }

            // Set health
            zombie.SetMaxHealth(health);
            zombie.SetHealth(health);

            // Clear inventory
            ItemContainer inventory = zombie.inventory.containerBelt as ItemContainer;
            inventory.itemList.Clear();

            // Add weapon
            var itemDef = ItemManager.FindItemDefinition("knife.bone");
            if (itemDef != null)
            {
                Item weaponItem = ItemManager.CreateByItemID(itemDef.itemid, 1, 1277364396);

                var zomObj = zombie as BaseCombatEntity;
                zomObj.GiveItem(weaponItem, BaseEntity.GiveItemReason.Generic);
            }

            //SendConsoleMessage($"(SpawnZombie) Zombie ({zombie.displayName}) spawned at x={pos.x}, y={pos.y}, z={pos.z}");
        }

        public void ClearAllZombies()
        {
            var allZombies = GetEntitiesByPrefabId(ZombiePrefabID);
            foreach (var zom in allZombies)
            {
                zom.Kill();
            }
        }
        #endregion

        #region Hooks and Events
        public void OnCheckZombieRadius(BasePlayer player)
        {
            if (!player.IsAlive()) return;

            // Check amount within radius
            var amountInRadius = GetEntitiesWithinRadius(player.transform.position, MinScanSpawnRadius, ZombiePrefabID);
            SendConsoleMessage($"(OnCheckZombieRadius) Zombie count near player: {amountInRadius?.Count}");

            if (amountInRadius?.Count >= MaxScanInRadius)
            {
                SendConsoleMessage("(OnCheckZombieRadius) Skipping spawning of zombies near player as threshold has been met.");
                return;
            }

            // Get the amount to spawn
            var amount = Random.Range(MinScanSpawnAmount, MaxScanSpawnAmount);
            SendConsoleMessage($"(OnCheckZombieRadius) Spawning {amount} zombies near player {player.displayName}");
            StartPlayerSpawning(player, amount, MinScanSpawnRadius, MaxScanSpawnRadius);
        }

        private BaseCorpse OnCorpsePopulate(BasePlayer npcPlayer, NPCPlayerCorpse corpse)
        {
            if (npcPlayer is ScarecrowNPC)
            {
                // Chance of getting good loot
                var chance = Random.Range(1, 4);
                if (chance == 3)
                {
                    return null;
                }
                
                // Otherwise default
                for (var i = 0; i < corpse.containers.Length; i++)
                {
                    var itemIndex = _zombieLootTable.FirstOrDefault(z => z.Index == i);
                    if (itemIndex is null)
                    {
                        break;
                    }

                    var item = ItemManager.FindItemDefinition(itemIndex.Name);
                    if (item != null)
                    {
                        corpse.containers[i].AddItem(item, Random.Range(itemIndex.MinAmount, itemIndex.MaxAmount));
                    }
                }

                // Note: Might be worth doing this, as many corpses can lag the server
                //corpse.Kill();
                return corpse;
            }

            return null;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            // Player killed zombie
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

                    var comEnt = entity as BaseCombatEntity;

                    IncreaseZombieKills(player);
                    SendPlayerMessage(player, $"You killed {Format(target.displayName, PrimaryRed)}");
                }
            }
        }
        #endregion

        #region Commands
        private void SpawnZombieCommand(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                SendPlayerMessage(player, "Nope :/");
                return;
            }

            if (args == null || args.Count() <= 0)
            {
                SendPlayerMessage(player, "Command: /zspawn <amount of zombies>");
                return;
            }

            var amount = int.Parse(args[0]);
            StartPlayerSpawning(player, amount, MinScanSpawnAmount, MaxScanSpawnAmount);
        }

        private void ClearZombiesCommand(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                SendPlayerMessage(player, "Nope :/");
                return;
            }

            ClearAllZombies();

            var amountOfZombies = GetEntitiesByPrefabId(ZombiePrefabID).Count;
            SendPlayerMessage(player, $"Zombies destroyed, population is now: {Format(amountOfZombies, PrimaryRed)}");
        }

        private void GetZombieCountCommand(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                SendPlayerMessage(player, "Nope :/");
                return;
            }

            var amountOfZombies = GetEntitiesByPrefabId(ZombiePrefabID).Count;
            SendPlayerMessage(player, $"Current Zombie Population: {Format(amountOfZombies, PrimaryRed)}");
        }

        private void RegenerateZombiesCommand(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                SendPlayerMessage(player, "Nope :/");
                return;
            }

            if (args == null || args.Count() <= 0)
            {
                SendPlayerMessage(player, "Command: /zre <amount of zombies>");
                return;
            }

            var amount = int.Parse(args[0]);
            var amountOfZombies = GetEntitiesByPrefabId(ZombiePrefabID).Count;

            SendPlayerMessage(player, $"Pre-Regenerate Pop: {Format(amountOfZombies, PrimaryRed)}");

            ClearAllZombies();
            StartMapSpawning(amount);

            amountOfZombies = GetEntitiesByPrefabId(ZombiePrefabID).Count;
            SendPlayerMessage(player, $"Post-Regenerate Pop: {Format(amountOfZombies, PrimaryRed)}");
        }

        private void GetZombieRadiusCountCommand(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                SendPlayerMessage(player, "Nope :/");
                return;
            }

            if (args ==  null || args.Count() <= 0)
            {
                SendPlayerMessage(player, "Command: /zradius <amount of zombies>");
                return;
            }

            var radius = int.Parse(args[0]);
            var radiusCount = GetEntitiesWithinRadius(player.transform.position, radius, ZombiePrefabID);
            SendPlayerMessage(player, $"There are {Format(radiusCount.Count, PrimaryRed)} zombies within a {Format(radius, Green)} radius of you");
        }
        #endregion
        #endregion

        #region Feature:Scientists (TODO)
        public void InitScientists()
        {


        }


        #endregion

        #region Feature:Stats
        public void IncreaseZombieKills(BasePlayer player)
        {
            var stats = _connectedPlayers
                .FirstOrDefault(p => p.Owner == player)
                .ZombieStats;
            stats.ZombieKills++;

            RegenerateUI(player);

            // Check to see if we announce a new title
            var rangeKey = _zombiePlayerTitles.Keys.FirstOrDefault(key => IsInRange(stats.ZombieKills, key));
            string playerTitle = _zombiePlayerTitles[rangeKey];

            if (playerTitle != stats.Title)
            {
                stats.Title = playerTitle;
                SendServerAnnouncement($"{Format(player.displayName, Green)} has earned the title {Format(playerTitle, Yellow)} from killing {Format(stats.ZombieKills, PrimaryRed)} zombies.");
            }
        }

        public void IncreasePlayerDeaths(BasePlayer player)
        {
            _connectedPlayers
                .FirstOrDefault(p => p.Owner == player)
                .ZombieStats
                .DeathByZombie++;

            RegenerateUI(player);
        }

        public int GetZombieKills(BasePlayer player ) => _connectedPlayers
                .FirstOrDefault(p => p.Owner == player)
                .ZombieStats
                .ZombieKills;

        public int GetDeathByZombie(BasePlayer player) => _connectedPlayers
                .FirstOrDefault(p => p.Owner == player)
                .ZombieStats
                .DeathByZombie;
        #endregion

        #region Feature:HUD
        private Dictionary<ulong, string> _playerUIs = new Dictionary<ulong, string>();
        private float HudTick = 1.0f;

        private void RegenerateUI(BasePlayer player)
        {
            if (player == null) return;

            var playerStats = _connectedPlayers
                .FirstOrDefault(p => p.Owner.OwnerID == player.OwnerID)
                .ZombieStats;

            // Destory previous UI
            DestroyPlayerUI(player);

            var panelName = $"{player.OwnerID}-zhud";
            var container = new CuiElementContainer();

            // Block container
            var panel = new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0" },
                RectTransform = { AnchorMin = "0.01 0.6", AnchorMax = "0.4 0.9" },
                CursorEnabled = false
            };
            container.Add(panel, "Hud", panelName);

            // Title
            var titleLabel = new CuiLabel
            {
                Text = { Text = $"<{playerStats.Title}> {player.displayName}", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.996 0.678 0 1" },
                RectTransform = { AnchorMin = "0.01 0.6", AnchorMax = "1 1" }
            };
            container.Add(titleLabel, panelName);

            // Stats
            var statsLabel = new CuiLabel
            {
                Text = { Text = playerStats.ToString(), FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "0.248 0.8 0 1" },
                RectTransform = { AnchorMin = "0.01 0.4", AnchorMax = "1 1" }
            };
            container.Add(statsLabel, panelName);

            // Online players
            StringBuilder sb = new StringBuilder();
            sb.Append($"Online Players\n");
            sb.Append($"--------------\n");
            foreach (var onlinePlayer in BasePlayer.activePlayerList)
            {
                sb.Append($"{onlinePlayer.displayName}\n");
            }

            var onlineLabel = new CuiLabel
            {
                Text = { Text = sb.ToString(), FontSize = 9, Align = TextAnchor.LowerLeft, Color = "#B4F800" },
                RectTransform = { AnchorMin = "0.01 0.03", AnchorMax = "1 1" }
            };
            container.Add(onlineLabel, panelName);

            // Add container to UI and cache
            CuiHelper.AddUi(player, container);
            _playerUIs[player.OwnerID] = panelName;
        }

        private void DestroyPlayerUI(BasePlayer player)
        {
            if (_playerUIs.TryGetValue(player.OwnerID, out string panelName))
            {
                CuiHelper.DestroyUi(player, panelName);
            }
        }
        #endregion

        #region Helpers and Others
        private bool IsInRange(int number, string range)
        {
            string[] parts = range.Split('-');
            if (parts.Length != 2)
            {
                return false;
            }

            int min = int.Parse(parts[0]);
            int max = int.Parse(parts[1]);

            return number >= min && number <= max;
        }

        private List<BaseEntity> GetEntitiesWithinRadius(Vector3 position, float radius, uint prefabId)
        {
            List<BaseEntity> entities = new List<BaseEntity>();
            Vis.Entities(position, radius, entities);

            // Filter entities by prefabID
            return entities.Where(entity => entity.prefabID == prefabId).ToList();
        }

        private Vector3 GetRandomPositionAroundPlayer(BasePlayer player, float min, float max)
        {
            Vector3 playerPos = player.transform.position;
            Vector3 position = Vector3.zero;

            var randomX = Random.Range(min, max);
            var randomZ = Random.Range(min, max);

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

        private Vector3 GetRandomPositionOnMap()
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

        private bool IsPosInObject(Vector3 position) => Physics.OverlapSphere(position, 0.5f, SpawnLayerMask).Length > 0;

        private bool IsPosInOcean(Vector3 position) => WaterLevel.GetWaterDepth(position, true, false) > 0.25f;

        private List<BaseEntity> GetEntitiesByPrefabId(uint prefabId)
        {
            var entities = new List<BaseEntity>();
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

        private void SendPlayerMessage(BasePlayer player, string msg) => Player.Message(player, msg, $"{ChatPrefix}<color={SecondaryRed}>");

        private void SendServerAnnouncement(string msg) => Server.Broadcast(msg, $"{ChatPrefix}<color={SecondaryRed}>");

        private void SendConsoleMessage(string msg) => Puts($"{Prefix} {msg}");

        private string Format(object msg, string colour) => $"<color={colour}>{msg}</color>";
        #endregion

        #region Models
        internal class ZombieLootTableItem
        {
            public int Index { get; set; }

            public string Name { get; set; }

            public int MinAmount { get; set; }

            public int MaxAmount { get; set; } 
        }

        internal class PlayerStats
        {
            public BasePlayer Owner { get; set; }

            public BaseStats BaseStats { get; set; } = new BaseStats();

            public PlayerZombieStats ZombieStats { get; set; } = new PlayerZombieStats();
        }

        internal class BaseStats
        {
            public DateTime JoinedTime { get; set; }
        }

        internal class PlayerZombieStats
        {
            public string Title { get; set; }

            public int ZombieKills { get; set; }

            public int DeathByZombie { get; set; }

            public PlayerZombieStats()
            {
                Title = _zombiePlayerTitles.FirstOrDefault().Value;
            }

            public override string ToString()
            {
                return $"Zombie Kills: {ZombieKills} \nDeaths by Zombie: {DeathByZombie}";
            }
        }
        #endregion
    }
}
