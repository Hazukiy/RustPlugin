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
using System.Threading;
using Network;
using System.Xml.Linq;
using static Oxide.Plugins.ZombieMod;
using System.Net.Mail;

namespace Oxide.Plugins
{
    [Info("ZombieMod", "My Back Hurts", "1.0.2")]
    [Description("Zombie mod for Rust")]
    class ZombieMod : RustPlugin
    {
        private const string Prefix = "[ZM]";
        private const string Green = "#1cbf68";
        private const string PrimaryRed = "#DF3434";
        private const string SecondaryRed = "#BD2323";
        private const string Yellow = "#DFEC08";
        private string Version = "v1.0.2";
        private string ChatPrefix = $"<color={SecondaryRed}>{Prefix}</color>";

        // The delay between general messages
        private const int ReportTimeDelay = 700;

        // Voteday related
        private bool _isVoteDayActive;
        private int _totalVoteDayCount;
        private List<BasePlayer> _voteDayPlayers;
        private Timer _voteDayTimeout;
        private Timer _voteDayCooldown;
        private DateTime _loadTime;

        // Cache
        private Dictionary<ulong, bool> godModePlayers;

        // Data
        private Hash<ulong, bool> noclipPlayers;
        private StoredData _storedData;

        #region Core:Plugin
        private void Init()
        {
            godModePlayers = new Dictionary<ulong, bool>();
            noclipPlayers = new Hash<ulong, bool>();
            _voteDayPlayers = new List<BasePlayer>();

            // Register general commands
            cmd.AddChatCommand("time", this, nameof(TimeCommand));
            cmd.AddChatCommand("voteday", this, nameof(VoteDayCommand));

            // Admin
            cmd.AddChatCommand("godmode", this, nameof(ToggleGodModeCommand));
            cmd.AddChatCommand("heal", this, nameof(HealCommand));
            cmd.AddChatCommand("savedata", this, nameof(SaveDataCommand));

            _loadTime = DateTime.Now;
            Version += $"-b{_loadTime:HH.mm.ss}";
        }

        private void Unload()
        {
            SaveData();

            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyPlayerUI(player);
            }

            _playerUIs.Clear();
            _voteDayPlayers.Clear();
            godModePlayers.Clear();
            _playerHudTimers.Clear();

            _playerHudTimers = null;
            _voteDayTimeout = null;
            _voteDayCooldown = null;
            _zombieSpawnTimer = null;
            godModePlayers = null;
        }
        #endregion

        #region Core:Server
        private void OnServerSave()
        {
            SaveData();
        }

        private void OnServerInitialized()
        {
            LoadData();

            // Start timer for broadcasting server time
            timer.Every(ReportTimeDelay, () =>
            {
                SendServerAnnouncement($"Server time is now: {Format($"{TOD_Sky.Instance.Cycle.DateTime:hh:mm} {TOD_Sky.Instance.Cycle.DateTime:tt}", PrimaryRed)}");
            });

            // Get zombies
            var zombieCount = GetEntitiesByPrefabId(ZombiePrefabID).Count;
            SendServerAnnouncement($"ZombieMod {Format(Version, Green)} successfully loaded with {Format(zombieCount, Green)} zombies currently on the map.");
            SendConsoleMessage($"ZombieMod {Version} successfully loaded.");

            // Initialise zombies
            InitZombies();

            // Rebuild each player
            foreach (var player in BasePlayer.activePlayerList)
            {
                BuildPlayer(player);
            }
            SaveData();

            // Reload everyone's UI 
            ReloadAllPlayerUI();
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

        #region Core:Player
        private object OnPlayerChat(BasePlayer player, string message, Chat.ChatChannel channel)
        {
            // Prevent override for emojis
            if (message.StartsWith(':') || channel != Chat.ChatChannel.Global)
            {
                return null;
            }

            var title = _storedData.PlayerData[player.userID];
            Server.Broadcast(message, $"[{channel}] (<color={title.ZombieStats.ZombieTitle.HexColour}>{title.ZombieStats.ZombieTitle.Title}</color>) <color={title.ZombieStats.ZombieTitle.NameColour}>{player.displayName}</color>", player.userID);
            return true; 
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
            //ReloadAllPlayerUI();
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

        public void BuildPlayer(BasePlayer player)
        {
            if (player == null) return;

            var playerData = _storedData.PlayerData[player.userID];
            if (playerData == null)
            {
                SendConsoleMessage($"{player.displayName} is new, creating new record");
                _storedData.PlayerData.Add(player.userID, new PlayerStats()
                {
                    Name = player.displayName,
                    FirstConnection = DateTime.Now
                });
            }

            _storedData.PlayerData[player.userID].Name = player.displayName;

            // Start zombie scan timer
            if (!_playerScanTimer.TryGetValue(player, out Timer scanTimer))
            {
                _playerScanTimer.Add(player, timer.Every(ScanTimerInterval, () => OnCheckZombieRadius(player)));
            }

            // Start hud timer
            if (!_playerHudTimers.TryGetValue(player, out Timer hudTimer))
            {
                _playerHudTimers.Add(player, timer.Every(_hudTick, () => RegenerateUI(player)));
            }
        }

        private void DestroyPlayer(BasePlayer player)
        {
            if (player == null) return;

            SaveData();

            // Remove zombie spawn timer
            if (_playerScanTimer.TryGetValue(player, out Timer playerTimer))
            {
                playerTimer.Destroy();
                _playerScanTimer.Remove(player);
            }

            // Remove update hud 
            if (_playerHudTimers.TryGetValue(player, out Timer hudTimer))
            {
                hudTimer.Destroy();
                _playerHudTimers.Remove(player);
            }

            SendConsoleMessage($"Cleaned up player {player.displayName} after disconnect.");
        }
        #endregion

        #region Core:Commands
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

        private void SaveDataCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (!player.IsAdmin)
            {
                SendPlayerMessage(player, "Nope :/");
                return;
            }

            SaveData();
            SendPlayerMessage(player, "Saved.");
        }
        #endregion

        #region Core:DataFileSystem 
        public void LoadData()
        {
            _storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
            SendConsoleMessage($"Loaded {_storedData.PlayerData.Count} records.");
        }

        public void SaveData()
        {
            SendConsoleMessage("Saving file data.");
            Interface.Oxide.DataFileSystem.WriteObject(Name, _storedData);
        }
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

        private const float DamageMultiplier = 0.3f; // How much damage a zombie does to the player
        private Timer _zombieSpawnTimer;
        private Dictionary<BasePlayer, Timer> _playerScanTimer;

        #region Zombie Properties
        // Zombie Map Spawn limits
        private const int MaxZombies = 1500;
        private const int MinPerRegen = 5; // Min amount of zombies to respawn around map
        private const int MaxPerRegen = 20; // How many zombies can be spawned on the map at a single time
        private float ZombieSpawnTime = (float)TimeSpan.FromMinutes(10).TotalSeconds; // How often to spawn zombies on the map 

        // Base Zombie Radius
        private const float MinSpawnRadius = 160.0f;
        private const float MaxSpawnRadius = 300.0f;

        // Zombie properties
        private const float MinZombieHealth = 50.0f;
        private const float MaxZombieHealth = 130.0f;
        private const float MinEasyZombieHealth = 20.0f;
        private const float MaxEasyZombieHealth = 40.0f;
        private const int MaxScrapLoot = 5;

        // Zombie scan
        private float ScanTimerInterval = (float)TimeSpan.FromMinutes(7).TotalSeconds;
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
            var itemDef = ItemManager.FindItemDefinition("knife.bone"); // 
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

        #region Feature:Stats
        public void IncreaseZombieKills(BasePlayer player)
        {
            var stats = _storedData.PlayerData[player.userID]
                .ZombieStats;
            stats.ZombieKills++;

            // Get the title
            var zombieTitle = ZombieTitle
                .GetZombieTitles()
                .FirstOrDefault(z => IsInRange(stats.ZombieKills, z.KillRange));

            if (zombieTitle.Title != stats.ZombieTitle.Title)
            {
                stats.ZombieTitle = zombieTitle;
                SendServerAnnouncement($"{Format(player.displayName, PrimaryRed)} has earned the title {Format(zombieTitle.Title, Green)} from killing {Format(stats.ZombieKills, PrimaryRed)} zombies.");
            }

            SaveData();

            // Regenerate all UIs
            ReloadAllPlayerUI();         
        }

        public void IncreasePlayerDeaths(BasePlayer player)
        {
            _storedData.PlayerData[player.userID]
                .ZombieStats
                .DeathByZombie++;

            SaveData();

            RegenerateUI(player);
        }

        public int GetZombieKills(BasePlayer player) => _storedData.PlayerData[player.userID]
                .ZombieStats
                .ZombieKills;

        public int GetDeathByZombie(BasePlayer player) => _storedData.PlayerData[player.userID]
                .ZombieStats
                .DeathByZombie;
        #endregion

        #region Feature:HUD
        private float _hudTick = (float)TimeSpan.FromMinutes(1).TotalSeconds;
        private Dictionary<BasePlayer, string> _playerUIs = new Dictionary<BasePlayer, string>();
        private Dictionary<BasePlayer, Timer> _playerHudTimers = new Dictionary<BasePlayer, Timer>();

        private void RegenerateUI(BasePlayer player)
        {
            if (player == null) return;

            // Destory previous UI
            DestroyPlayerUI(player);

            // Get online players
            var playerStats = _storedData.PlayerData[player.userID];
            var onlinePlayers = GetOnlinePlayers();
            var panelName = $"{player.userID}-zhud";

            // Add UI
            CuiHelper.AddUi(player, new CuiElementContainer
            {
                // PANEL: Main hud 
                {
                    new CuiPanel
                    {
                        Image = { Color = "0.1 0.1 0.1 0" },
                        RectTransform = { AnchorMin = "0.01 0", AnchorMax = "0.2 1" },
                        CursorEnabled = false
                    },
                    "Hud",
                    panelName
                },

                // LABEL: Version
                {
                    new CuiLabel
                    {
                        Text = { Text = $"ZombieMod {Version}", FontSize = 9, Align = TextAnchor.LowerLeft, Color = "0.470 0.736 0.111 0.6" },
                        RectTransform = { AnchorMin = "0.01 0.01", AnchorMax = "1 1" }
                    },
                    panelName
                },

                // LABEL: Name Title
                {
                    new CuiLabel
                    {
                        Text = { Text = $"Welcome, {playerStats.Name}", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.870 0.236 0.111 1" },
                        RectTransform = { AnchorMin = "0.01 0.645", AnchorMax = "1 1" }
                    },
                    panelName
                },

                // LABEL: Zombie Title
                {
                    new CuiLabel
                    {
                        Text = { Text = $"Level {ZombieTitle.GetTitleIndex(playerStats.ZombieStats.ZombieTitle.Title)+1} - {playerStats.ZombieStats.ZombieTitle.Title}", FontSize = 10, Align = TextAnchor.MiddleLeft, Color = playerStats.ZombieStats.ZombieTitle.Colour },
                        RectTransform = { AnchorMin = "0.01 0.61", AnchorMax = "1 1" }
                    },
                    panelName
                },

                // LABEL: Stats
                {
                    new CuiLabel
                    {
                        Text = { Text = playerStats.ToString(), FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "0.870 0.536 0.111 1" },
                        RectTransform = { AnchorMin = "0.01 0.53", AnchorMax = "1 1" }
                    },
                    panelName
                },

                // LABEL: Online players
                {
                    new CuiLabel
                    {
                        Text = { Text = onlinePlayers, FontSize = 8, Align = TextAnchor.MiddleLeft, Color = "0.170 0.536 0.111 1" },
                        RectTransform = { AnchorMin = "0.01 0.3", AnchorMax = "1 1" }
                    },
                    panelName
                }
            });

            // Add container to UI and cache
            _playerUIs[player] = panelName;
        }

        private void DestroyPlayerUI(BasePlayer player)
        {
            if (_playerUIs.TryGetValue(player, out string panelName))
            {
                CuiHelper.DestroyUi(player, panelName);
            }
        }

        private void ReloadAllPlayerUI()
        {
            // Get online players only
            var online = BasePlayer.activePlayerList.Where(p => p != null && p.IsConnected).ToList();
            if (online != null)
            {
                foreach (var player in online)
                {
                    RegenerateUI(player);
                }
            }
        }

        private string GetOnlinePlayers()
        {
            // Online players
            var sb = new StringBuilder();
            sb.Append($"Online Players\n");
            sb.Append($"----------------\n");

            var order = 1;
            foreach (var onlinePlayer in _storedData.PlayerData.OrderByDescending(p => p.Value.ZombieStats.ZombieKills))
            {
                sb.Append($"{order}. {onlinePlayer.Value.Name} ({onlinePlayer.Value.ZombieStats.ZombieKills} kills {onlinePlayer.Value.ZombieStats.DeathByZombie} deaths)\n");
                order++;
            }

            return sb.ToString();
        }

        #endregion

        #region Feature:Scientists (TODO)
        public void InitScientists()
        {


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

        internal class ZombieTitle
        {
            public string Title { get; set; }

            public string KillRange { get; set; }

            public string Colour { get; set; }

            public string HexColour { get; set; }

            public string NameColour { get; set; }

            private static (string, string) GenerateColour(double progress, double startR, double startG, double startB, double endR, double endG, double endB)
            {
                double r = startR + progress * (endR - startR);
                double g = startG + progress * (endG - startG);
                double b = startB + progress * (endB - startB);

                string rgbaColour = $"{r:F3} {g:F3} {b:F3} 1";
                string hexColour = $"#{(int)(r * 255):X2}{(int)(g * 255):X2}{(int)(b * 255):X2}";

                return (rgbaColour, hexColour);
            }

            public static int GetTitleIndex(string title)
            {
                var titles = GetZombieTitles();
                for (int i = 0; i < titles.Count; i++)
                {
                    if (titles[i].Title.Equals(title, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
                return -1; // Return -1 if the title is not found
            }

            public static List<ZombieTitle> GetZombieTitles()
            {
                var titles = new List<ZombieTitle>
                {
                    new ZombieTitle { KillRange = "0-9", Title = "Zombie Bait" },
                    new ZombieTitle { KillRange = "10-19", Title = "Novice Survivor" },
                    new ZombieTitle { KillRange = "20-29", Title = "Grizzled Fighter" },
                    new ZombieTitle { KillRange = "30-39", Title = "Undead Slayer" },
                    new ZombieTitle { KillRange = "40-49", Title = "Zombie Hunter" },
                    new ZombieTitle { KillRange = "50-59", Title = "Apocalypse Survivor" },
                    new ZombieTitle { KillRange = "60-69", Title = "Zombie Annihilator" },
                    new ZombieTitle { KillRange = "70-79", Title = "Ultimate Undead Slayer" },
                    new ZombieTitle { KillRange = "80-89", Title = "Zombie Exterminator" },
                    new ZombieTitle { KillRange = "90-99", Title = "Legendary Apocalypse Survivor" },
                    new ZombieTitle { KillRange = "100-109", Title = "Master of the Undead" },
                    new ZombieTitle { KillRange = "110-119", Title = "Zombie Overlord" },
                    new ZombieTitle { KillRange = "120-129", Title = "Undead Conqueror" },
                    new ZombieTitle { KillRange = "130-139", Title = "Lord of the Wasteland" },
                    new ZombieTitle { KillRange = "140-149", Title = "Dread Reaper" },
                    new ZombieTitle { KillRange = "150-159", Title = "Vanquisher of the Undead" },
                    new ZombieTitle { KillRange = "160-169", Title = "Necropolis Dominator" },
                    new ZombieTitle { KillRange = "170-179", Title = "Champion of the Apocalypse" },
                    new ZombieTitle { KillRange = "180-189", Title = "Supreme Undead Ruler" },
                    new ZombieTitle { KillRange = "190-199", Title = "Immortal Zombie Slayer" },
                    new ZombieTitle { KillRange = "200-209", Title = "Eternal Apocalypse Conqueror" },
                    new ZombieTitle { KillRange = "210-219", Title = "Mythical Undead Destroyer" },
                    new ZombieTitle { KillRange = "220-229", Title = "Divine Zombie Vanquisher" },
                    new ZombieTitle { KillRange = "230-239", Title = "Godlike Apocalypse Champion" },
                    new ZombieTitle { KillRange = "240-249", Title = "Ultimate Wasteland Hero" },
                    new ZombieTitle { KillRange = "250-259", Title = "Celestial Undead Master" },
                    new ZombieTitle { KillRange = "260-269", Title = "Infinite Zombie Annihilator" },
                    new ZombieTitle { KillRange = "270-279", Title = "Transcendent Apocalypse Warrior" },
                    new ZombieTitle { KillRange = "280-289", Title = "Ethereal Undead Monarch" },
                    new ZombieTitle { KillRange = "290-299", Title = "Legendary Eternal Slayer" },
                    new ZombieTitle { KillRange = "300+", Title = "Apocalyptic Demigod" }
                };

                int maxIndex = titles.Count - 1;
                for (int i = 0; i <= maxIndex; i++)
                {
                    double progress = (double)i / maxIndex; // Calculate progress from 0 to 1

                    // For the main colour: from darker yellow (255, 204, 0) to bright red (255, 0, 0)
                    var (colourRgba, colourHex) = GenerateColour(progress, 1.0, 204.0 / 255.0, 0.0, 1.0, 0.0, 0.0);
                    titles[i].Colour = colourRgba;
                    titles[i].HexColour = colourHex;

                    // For the name colour: from dark red (139, 0, 0) to bright red (255, 0, 0)
                    var (nameColourRgba, nameColourHex) = GenerateColour(progress, 139.0 / 255.0, 0.0, 0.0, 1.0, 0.0, 0.0);
                    titles[i].NameColour = nameColourHex;
                }

                return titles;
            }
        }

        internal class ZombieLootTableItem
        {
            public int Index { get; set; }

            public string Name { get; set; }

            public int MinAmount { get; set; }

            public int MaxAmount { get; set; } 
        }

        internal class PlayerStats
        {
            public string Name { get; set; }

            public DateTime FirstConnection { get; set; }

            public PlayerZombieStats ZombieStats { get; set; } = new PlayerZombieStats();

            public override string ToString()
            {
                var totalMinutes = DateTime.Now - FirstConnection;

                return $"Time on Server: {Math.Floor(totalMinutes.TotalMinutes)} Minutes\n{ZombieStats}";
            }
        }

        internal class PlayerZombieStats
        {
            public ZombieTitle ZombieTitle { get; set; }

            public int ZombieKills { get; set; }

            public int DeathByZombie { get; set; }

            public PlayerZombieStats()
            {
                ZombieTitle = ZombieTitle.GetZombieTitles().FirstOrDefault();
            }

            public override string ToString()
            {
                return $"Zombie Kills: {ZombieKills} \nDeaths by Zombie: {DeathByZombie}";
            }
        }

        internal class StoredData
        {
            public readonly Hash<ulong, PlayerStats> PlayerData = new Hash<ulong, PlayerStats>();
        }
        #endregion
    }
}
