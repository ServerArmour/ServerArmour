using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using Time = Oxide.Core.Libraries.Time;

#if RUST
using UnityEngine; 
#endif

namespace Oxide.Plugins {
    [Info("ServerArmour", "Pho3niX90", "0.0.62")]
    [Description("Protect your server! Auto ban known hacker, scripter and griefer accounts, and notify server owners of threats.")]
    class ServerArmour : CovalencePlugin {

        #region Variables
        Dictionary<string, ISAPlayer> _playerData = new Dictionary<string, ISAPlayer>();
        private Time _time = GetLibrary<Time>();
        private double cacheLifetime = 300; // minutes
        string[] groups;
        private ISAConfig config;
        static string thisServerIp;
        string settingsVersion = "0.0.1";
        string specifier = "G";
        CultureInfo culture = CultureInfo.CreateSpecificCulture("en-US");
        #region Permissions
        const string PermissionToBan = "serverarmour.ban";
        #endregion
        #endregion



        #region Plugins
        [PluginReference]
        Plugin BetterChat;
#if RUST
        [PluginReference]
        Plugin Arkan;
#endif
        #endregion

        #region Hooks
        void Init() {
            Puts("Server Armour is being initialized.");
            config = Config.ReadObject<ISAConfig>();
            if (!config.Version.Equals(settingsVersion)) UpgradeConfig(config.Version, settingsVersion);
            thisServerIp = server.Address.ToString();

            if (config.ServerVersion.Equals(server.Version.ToString(), StringComparison.InvariantCultureIgnoreCase)) {
                config.ServerName = server.Name;
                config.ServerPort = server.Port;
                config.ServerVersion = server.Version;
                SaveData();
                RegisterTag();
            }

            LoadDefaultMessages();
            CheckGroups();
            LoadData();
        }

        void OnServerInitialized() {
            RegisterTag();
        }

        void Loaded() {
            Puts("Checking all known users.");

            int playerTotal = 0;
            foreach (IPlayer player in players.All) {
                if (player != null) {
                    GetPlayerBans(player, true, player.IsConnected);
                }
                playerTotal++;
            }

            timer.Once(30f, () => CheckLocalBans());

            Puts("Checking " + playerTotal + " players");
            Puts("Server Armour finished initializing.");
            RegisterTag();
        }

        void Unload() {
            Puts("Server Armour unloading, will now save all data.");
            SaveConfig();
            SaveData();
            Puts("Server Armour finished unloaded.");
        }

        void OnUserConnected(IPlayer player) {
            Puts($"{player.Name} ({player.Id}) connected from {player.Address}");
            GetPlayerBans(player, false, player.IsConnected);
            timer.Once(30, () => GetPlayerReport(player));
        }

        void OnUserDisconnected(IPlayer player) {
            Puts($"{player.Name} ({player.Id}) disconnected");
        }

        bool CanUserLogin(string name, string id, string ip) {
            return !AssignGroupsAndBan(players.FindPlayer(name));
        }

        /* Unused atm, will uncomment as needed
                void OnUserApproved(string name, string id, string ip) {
                    Puts($"{name} ({id}) at {ip} has been approved to connect");
                }

                void OnUserNameUpdated(string id, string oldName, string newName) {
                    Puts($"Player name changed from {oldName} to {newName} for ID {id}");
                }

                void OnUserKicked(IPlayer player, string reason) {
                    Puts($"Player {player.Name} ({player.Id}) was kicked");
                }

                void OnUserBanned(string name, string id, string ip, string reason) {
                    Puts($"Player {name} ({id}) at {ip} was banned: {reason}");
                }

                void OnUserUnbanned(string name, string id, string ip) {
                    Puts($"Player {name} ({id}) at {ip} was unbanned");
                }
        */

        void OnPluginLoaded(Plugin plugin) {
            if (plugin.Title == "BetterChat") RegisterTag();
        }
        #endregion

        #region WebRequests
        void GetPlayerBans(IPlayer player, bool onlyGetUncached = false, bool connectedTime = true) {
            bool isCached = IsPlayerCached(player.Id);

            if (isCached && !onlyGetUncached) {
                uint currentTimestamp = _time.GetUnixTimestamp();
                double minutesOld = (currentTimestamp - GetPlayerCache(player.Id).cacheTimestamp) / 60.0 / 1000.0;
                bool oldCache = cacheLifetime <= minutesOld;
                GetPlayerCache(player.Id).lastConnected = currentTimestamp;
                if (!oldCache) return; //user already cached, therefore do not check again before cache time laps.
            }

            string url = $"http://io.serverarmour.com/checkUser?steamid={player.Id}&username={player.Name}&ip={player.Address}" + ServerGetString();
            webrequest.Enqueue(url, null, (code, response) => {
                if (code != 200 || response == null) {
                    Puts($"Couldn't get an answer from ServerArmour.com!");
                    return;
                }
                ISAPlayer isaPlayer = JsonConvert.DeserializeObject<ISAPlayer>(response);
                isaPlayer.cacheTimestamp = _time.GetUnixTimestamp();
                isaPlayer.lastConnected = _time.GetUnixTimestamp();
                if (!IsPlayerCached(isaPlayer.steamid)) {
                    AddPlayerCached(isaPlayer);
                } else {
                    SetPlayerBanData(isaPlayer);
                }

                GetPlayerReport(isaPlayer, player.IsConnected);

                SaveData();

            }, this, RequestMethod.GET);
        }

        void AddBan(IPlayer player, string banreason) {
            string dateTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm").ToString();
            string url = $"http://io.serverarmour.com/addBan?steamid={player.Id}&username={player.Name}&ip={player.Address}&reason={banreason}&dateTime={dateTime}" + ServerGetString();
            webrequest.Enqueue(url, null, (code, response) => {
                if (code != 200 || response == null) {
                    Puts($"Couldn't get an answer from ServerArmour.com!");
                    return;
                }

                if (IsPlayerCached(player.Id.ToString())) {
                    Puts($"{player.Name} has ban cached, now updating.");
                    AddPlayerBanData(player, new ISABan { serverName = server.Name, date = dateTime, reason = banreason, serverIp = thisServerIp });
                } else {
                    Puts($"{player.Name} had no ban data cached, now creating.");
                    AddPlayerCached(player,
                        new ISAPlayer {
                            steamid = player.Id.ToString(),
                            username = player.Name,
                            serverBanCount = 1,
                            cacheTimestamp = _time.GetUnixTimestamp(),
                            lastConnected = _time.GetUnixTimestamp(),
                            serverBanData = new List<ISABan> { new ISABan { serverName = server.Name, date = dateTime, reason = banreason, serverIp = thisServerIp } }
                        });
                }
                SaveData();
            }, this, RequestMethod.GET);
        }

        bool IsBanned(string steamid) {
            if (IsPlayerCached(steamid)) {
                ISAPlayer isaPlayer = GetPlayerCache(steamid);
                foreach (ISABan ban in isaPlayer.serverBanData) {
                    if (ban.serverIp.Equals(thisServerIp, StringComparison.InvariantCultureIgnoreCase)) {
                        return true;
                    }
                }
            }
            return false;
        }

        string GetBanReason(ISAPlayer isaPlayer) {
            foreach (ISABan ban in isaPlayer.serverBanData) {
                if (ban.serverIp.Equals(thisServerIp, StringComparison.InvariantCultureIgnoreCase)) {
                    return ban.reason;
                }
            }
            return "UNKOWN";
        }

        #endregion

        #region Commands
        [Command("sa.clb")]
        void SCmdCheckLocalBans(IPlayer player, string command, string[] args) {
            CheckLocalBans();
        }

        [Command("ban", "playerban", "sa.ban")]
        void SCmdBan(IPlayer player, string command, string[] args) {
            if (!hasPermission(player, PermissionToBan)) {
                player.Reply(GetMsg("NoPermission"));
                return;
            }

            ///ban "player name" "reason"

            if (args == null || (args.Length != 2)) {
                player.Reply(GetMsg("BanSyntax"));
                return;
            }

            IPlayer iPlayer = players.FindPlayer(args[0]);
            ISAPlayer isaPlayer;

            if (!IsPlayerCached(iPlayer.Id)) {
                isaPlayer = new ISAPlayer().CreatePlayer(iPlayer);
                AddPlayerCached(isaPlayer);
            } else {
                isaPlayer = GetPlayerCache(args[0]);
            }

            if (BanPlayer(iPlayer,
                new ISABan {
                    serverName = server.Name,
                    serverIp = thisServerIp,
                    reason = args[1],
                    date = new Time().GetDateTimeFromUnix(new Time().GetUnixTimestamp()).ToShortDateString()
                })) {

                string msg = GetMsg("Player Now Banned", new Dictionary<string, string> { ["player"] = isaPlayer.username, ["reason"] = args[1] });
                if (config.BroadcastNewBans) {
                    server.Broadcast(msg);
                } else {
                    player.Reply(msg);
                }
            }
        }

        [Command("sa.cp")]
        void SCmdCheckPlayer(IPlayer player, string command, string[] args) {
            Puts("arg length " + args.Length); Puts(command);
            string playerArg = (args.Length == 0) ? player.Id : args[0];
            bool forceUpdate = false;

            if (args.Length > 1) {
                bool.TryParse(args[1], out forceUpdate);
            }

            IPlayer playerToCheck = players.FindPlayer(playerArg.Trim());
            if (playerToCheck == null) {
                player.Reply(GetMsg("PlayerNotFound", new Dictionary<string, string> { ["player"] = playerArg }));
            }

            if (IsPlayerCached(playerToCheck.Id) && forceUpdate) {
                GetPlayerBans(playerToCheck, !forceUpdate);
            }

            CheckLocalBans();
            GetPlayerReport(playerToCheck, player);
        }
        #endregion

        #region Ban System
        bool BanPlayer(IPlayer iPlayer, ISABan ban) {
            AddBan(iPlayer, ban.reason);
            return true;
        }
        #endregion

        #region Data Handling
        bool IsPlayerDirty(string steamid) {
            ISAPlayer isaPlayer = GetPlayerCache(steamid);
            return IsPlayerCached(steamid) && (isaPlayer.serverBanCount > 0 || isaPlayer.steamData.CommunityBanned > 0 || isaPlayer.steamData.NumberOfGameBans > 0 || isaPlayer.steamData.VACBanned > 0);
        }

        bool IsPlayerCached(string steamid) => _playerData.ContainsKey(steamid);
        bool DeletePlayerCache(string steamid) => _playerData.Remove(steamid);
        void AddPlayerCached(ISAPlayer isaplayer) => _playerData.Add(isaplayer.steamid, isaplayer);
        void AddPlayerCached(IPlayer iplayer, ISAPlayer isaplayer) => _playerData.Add(iplayer.Id, isaplayer);
        ISAPlayer GetPlayerCache(string steamid) => IsPlayerCached(steamid) ? _playerData[steamid] : null;
        List<ISABan> GetPlayerBanData(string steamid) => _playerData[steamid].serverBanData;
        int GetPlayerBanDataCount(string steamid) => _playerData[steamid].serverBanData.Count;
        void SetPlayerBanData(ISAPlayer isaplayer) => _playerData[isaplayer.steamid].serverBanData = isaplayer.serverBanData;
        void AddPlayerBanData(IPlayer iplayer, ISABan isaban) => _playerData[iplayer.Id].serverBanData.Add(isaban);
        IPlayer FindIPlayer(string identifier) => players.FindPlayer(identifier);

        void GetPlayerReport(IPlayer player) {
            ISAPlayer isaPlayer = GetPlayerCache(player.Id);
            if (isaPlayer != null) GetPlayerReport(isaPlayer, player.IsConnected);
        }

        void GetPlayerReport(IPlayer player, IPlayer cmdplayer) {
            ISAPlayer isaPlayer = GetPlayerCache(player.Id);
            if (isaPlayer != null) GetPlayerReport(isaPlayer, player.IsConnected, true, cmdplayer);
        }

        void GetPlayerReport(IPlayer player, bool isCommand = false) {
            ISAPlayer isaPlayer = GetPlayerCache(player.Id);
            if (isaPlayer != null) GetPlayerReport(isaPlayer, player.IsConnected, isCommand);
        }


        void GetPlayerReport(ISAPlayer isaPlayer, bool isConnected = true, bool isCommand = false, IPlayer cmdPlayer = null) {

            if (IsPlayerDirty(isaPlayer.steamid) || isCommand) {
                string report = GetMsg("User Dirty MSG",
                        new Dictionary<string, string> {
                            ["status"] = IsPlayerDirty(isaPlayer.steamid) ? "dirty" : "clean",
                            ["steamid"] = isaPlayer.steamid,
                            ["username"] = isaPlayer.username,
                            ["serverBanCount"] = isaPlayer.serverBanCount.ToString(),
                            ["NumberOfGameBans"] = isaPlayer.steamData.NumberOfGameBans.ToString(),
                            ["NumberOfVACBans"] = isaPlayer.steamData.NumberOfVACBans.ToString(),
                            ["EconomyBan"] = isaPlayer.steamData.EconomyBan.ToString()
                        });
                if (config.BroadcastPlayerBanReport && isConnected && !isCommand) {
                    server.Broadcast(report.Replace(isaPlayer.steamid + ":", string.Empty).Replace(isaPlayer.steamid, string.Empty));
                }
                if (isCommand) {
                    cmdPlayer.Reply(report.Replace(isaPlayer.steamid + ":", string.Empty).Replace(isaPlayer.steamid, string.Empty));
                }
                Puts(report);
            }
        }

        T GetConfig<T>(string name, T defaultValue) => Config[name] == null ? defaultValue : (T)Convert.ChangeType(Config[name], typeof(T));
        protected override void LoadDefaultConfig() {
            LogWarning("Creating a new configuration file");
            Config.WriteObject(UpgradeConfig(), true);
            SaveConfig();
        }

        ISAConfig UpgradeConfig(string oldVersion = "", string newVersion = "") {
            return new ISAConfig {
                Version = settingsVersion,
                Debug = false,

                ShowProtectedMsg = true,
                AutoBanOn = true,
                BroadcastPlayerBanReport = true,
                BroadcastNewBans = true,
                ServerAdminShareDetails = true,

                BetterChatDirtyPlayerTag = "DIRTY",
                AutoBanGroup = "serverarmour.bans",
                WatchlistGroup = "serverarmour.watchlist",
                AutoBanReasonKeywords = new string[] { "*aimbot*", "*esp*", "*hack*", "*script*" },

                AutoBanCeiling = 5,
                AutoVacBanCeiling = 2,
                WatchlistCeiling = 1,


                ServerName = server.Name,
                ServerPort = server.Port,
                ServerVersion = server.Version,
                ServerAdminName = string.Empty,
                ServerAdminEmail = string.Empty,
                ServerApiKey = "FREE"
            };
        }

        private void LogDebug(string txt) {
            if (config.Debug) Puts(txt);
        }

        void SaveData() {
            if (_playerData.Count > 0)
                Interface.Oxide.DataFileSystem.WriteObject<Dictionary<string, ISAPlayer>>("ServerArmour", _playerData, true);
        }

        void LoadData() {
            if (Interface.Oxide.DataFileSystem.ExistsDatafile("ServerArmour")) {
                _playerData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, ISAPlayer>>("ServerArmour");
                Puts("DATA loaded");
            } else {
                SaveData();
            }
        }

        void CheckGroups() {
            Puts("Registering groups");

            string[] groups = permission.GetGroups();

            Puts("Checking if config groups exists.");

            string autobanGroup = GetConfig("AutoBanGroup", "serverarmour.bans");
            string watchlistGroup = GetConfig("AutoBanGroup", "serverarmour.watchlists");

            if (!permission.GroupExists(autobanGroup)) {
                permission.CreateGroup(autobanGroup, "Server Armour Autobans", 0);
            }

            if (!permission.GroupExists(watchlistGroup)) {
                permission.CreateGroup(watchlistGroup, "Server Armour Watchlist", 0);
            }
        }

        string ServerGetString() {
            return $"&sn={config.ServerName}&sp={config.ServerPort}&an={config.ServerAdminName}&ae={config.ServerAdminEmail}&gameId={covalence.ClientAppId}&gameName={covalence.Game}";
        }


        bool isIPAddress(string arg) {
            int subIP;
            string[] strArray = arg.Split('.');
            if (strArray.Length != 4) {
                return false;
            }
            foreach (string str in strArray) {
                if (str.Length == 0) {
                    return false;
                }
                if (!int.TryParse(str, out subIP) && str != "*") {
                    return false;
                }
                if (!(str == "*" || (subIP >= 0 && subIP <= 255))) {
                    return false;
                }
            }
            return true;
        }

        void CheckLocalBans() {
#if RUST
            foreach (ServerUsers.User usr in ServerUsers.GetAll(ServerUsers.UserGroup.Banned)) {

                bool containsMyBan = false;


                if (IsPlayerCached(usr.steamid.ToString(specifier, culture))) {
                    List<ISABan> bans = GetPlayerBanData(usr.steamid.ToString(specifier, culture));
                    foreach (ISABan ban in bans) {
                        if (ban.serverIp.Equals(thisServerIp, StringComparison.InvariantCultureIgnoreCase)) {
                            containsMyBan = true;
                            break;
                        }
                    }
                }


                if (!containsMyBan) {
                    IPlayer player = covalence.Players.FindPlayer(usr.steamid.ToString(specifier, culture));
                    AddBan(player, usr.notes);
                }
            }
#endif
        }


        bool AssignGroupsAndBan(IPlayer player) {
            try {
                ISAPlayer isaPlayer = IsPlayerCached(player.Id) ? GetPlayerCache(player.Id) : null;
                if (isaPlayer == null) return false;

                if (config.AutoBanOn) {
                    if (config.AutoVacBanCeiling <= isaPlayer.steamData.NumberOfVACBans) {

                        return true;
                    }
                }

                if (config.WatchlistCeiling <= isaPlayer.serverBanCount) {

                }

                if (IsBanned(isaPlayer.steamid)) {
                    server.Broadcast($"{GetChatTag()} {isaPlayer.username} wasn't allowed to connect\nReason: {GetBanReason(isaPlayer)}");
                    return true;
                }
            } catch (NullReferenceException nre) {
                return false;
            }
            return false;
        }

        bool ShouldBan(ISAPlayer isaPlayer) {
            return config.AutoBanOn && (config.AutoVacBanCeiling <= isaPlayer.steamData.NumberOfVACBans || config.AutoBanCeiling < isaPlayer.serverBanCount);
        }
        #endregion

        #region API Hooks
        private int API_GetServerBanCount(string steamid) => IsPlayerCached(steamid) ? GetPlayerBanDataCount(steamid) : 0;
        private bool API_GetIsVacBanned(string steamid) => IsPlayerCached(steamid) ? GetPlayerCache(steamid).steamData.VACBanned == 1 : false;
        private bool API_GetIsCommunityBanned(string steamid) => IsPlayerCached(steamid) ? GetPlayerCache(steamid).steamData.CommunityBanned == 1 : false;
        private int API_GetVacBanCount(string steamid) => IsPlayerCached(steamid) ? GetPlayerCache(steamid).steamData.NumberOfVACBans : 0;
        private int API_GetGameBanCount(string steamid) => IsPlayerCached(steamid) ? GetPlayerCache(steamid).steamData.NumberOfGameBans : 0;
        private string API_GetEconomyBanStatus(string steamid) => IsPlayerCached(steamid) ? GetPlayerCache(steamid).steamData.EconomyBan : "none";
        private bool API_GetIsPlayerDirty(string steamid) => IsPlayerCached(steamid) && GetPlayerBanDataCount(steamid) > 0 || GetPlayerCache(steamid).steamData.VACBanned == 1;
        #endregion

        #region Localization
        string GetMsg(string msg, Dictionary<string, string> rpls = null) {
            string message = lang.GetMessage(msg, this);

            if (rpls != null)
                foreach (var rpl in rpls)
                    message = message.Replace($"{{{rpl.Key}}}", rpl.Value);

            return message;
        }

        protected override void LoadDefaultMessages() {
            lang.RegisterMessages(new Dictionary<string, string> {
                ["Protected MSG"] = "Server protected by ServerArmour",
                ["User Dirty MSG"] = "<color=#008080ff>Server Armour Report:\n {steamid}:{username}</color> is {status}.\n <color=#ff0000ff>Server Bans:</color> {serverBanCount}\n <color=#ff0000ff>Game Bans:</color> {NumberOfGameBans}\n <color=#ff0000ff>Vac Bans:</color> {NumberOfVACBans}\n <color=#ff0000ff>Economy Status:</color> {EconomyBan}",
                ["Command sa.cp Error"] = "Wrong format, example: /sa.cp usernameORsteamid trueORfalse",
                ["Arkan No Recoil Violation"] = "<color=#ff0000>{player}</color> received an Arkan no recoil violation.\n<color=#ff0000>Violation</color> #{violationNr}, <color=#ff0000>Weapon:</color> {weapon}, <color=#ff0000>Ammo:</color> {ammo}, <color=#ff0000>Shots count:</color> {shots}\n Admins will investigate ASAP, please have handcams ready.\n This might be a false-positive, but all violations need to be investigated.",
                ["Arkan Aimbot Violation"] = "<color=#ff0000>{player}</color> received an Arkan aimbot violation.\n<color=#ff0000>Violation</color>  #{violationNr}, <color=#ff0000>Weapon:</color> {weapon}, <color=#ff0000>Ammo:</color> {ammo}, <color=#ff0000>Shots count:</color> {shots}\n Admins will investigate ASAP, please have handcams ready.\n This might be a false-positive, but all violations need to be investigated.",
                ["Arkan In Rock Violation"] = "<color=#ff0000>{player}</color> received an Arkan in rock violation.\n<color=#ff0000>Violation</color>  #{violationNr}, <color=#ff0000>Weapon:</color> {weapon}, <color=#ff0000>Ammo:</color> {ammo}\n Admins will investigate ASAP, please have handcams ready.\n This might be a false-positive, but all violations need to be investigated.",
                ["Player Now Banned"] = "<color=#ff0000>{player}</color> has been banned\n<color=#ff0000>Reason: </color> {reason}"
            }, this);
        }

        bool hasPermission(IPlayer player, string permissionName) {
            if (player.IsAdmin) return true;
            return permission.UserHasPermission(player.Id, permissionName);
        }
        #endregion

        #region Plugins methods
        string GetChatTag() => "<color=#008080ff>[Server Armour]: </color>";
        void RegisterTag() {
            BetterChat?.Call("API_RegisterThirdPartyTitle", new object[] { this, new Func<IPlayer, string>(GetTag) });
        }

        string GetTag(IPlayer player) {
            if (BetterChat != null && IsPlayerDirty(player.Id) && config.BetterChatDirtyPlayerTag.Length > 0) {
                return $"[#FFA500][{config.BetterChatDirtyPlayerTag}][/#]";
            } else {
                return string.Empty;
            }
        }
        #endregion

        #region Classes 
        public class ISAPlayer {
            public string steamid;
            public string username;
            public ISASteamData steamData;
            public int serverBanCount;
            public List<ISABan> serverBanData;
            public uint cacheTimestamp;
            public uint lastConnected;
#if RUST
            public PlayerArkanViolationsData arkanInfo;
            public List<InRockViolationsData> arkanIRData;
            public List<AIMViolationData> arkanABData;
            public List<NoRecoilViolationData> arkanNRData;

            public void AddArkanData(InRockViolationsData data) {
                arkanIRData.Add(data);
            }
            public void AddArkanData(AIMViolationData data) {
                arkanABData.Add(data);
            }
            public void AddArkanData(NoRecoilViolationData data) {
                arkanNRData.Add(data);
            }
#endif
            public ISAPlayer CreatePlayer(IPlayer bPlayer) {
                steamid = bPlayer.Id;
                username = bPlayer.Name;
                cacheTimestamp = new Time().GetUnixTimestamp();
                lastConnected = new Time().GetUnixTimestamp();
                return this;
            }
        }

        public class ISABan {
            public string banId;
            public string serverName;
            public string serverIp;
            public string reason;
            public string date;
        }

        public class ISASteamData {
            public int CommunityBanned;
            public int VACBanned;
            public int NumberOfVACBans;
            public int DaysSinceLastBan;
            public int NumberOfGameBans;
            public string EconomyBan;
            public string BansLastCheckUTC;
        }

        private class ISAConfig {
            public string Version;
            public bool Debug; //should always be false, unless explicitly asked to turn on, will cause performance issue when on.
            public bool ShowProtectedMsg; // Show the protected by ServerArmour msg?
            public string AutoBanGroup; // the group name that banned users should be added in
            public bool AutoBanOn; // turn auto banning on or off. 
            public int AutoBanCeiling; // Auto ban players with X amount of previous bans.
            public int AutoVacBanCeiling; //  Auto ban players with X amount of vac bans.
            public string[] AutoBanReasonKeywords; // auto ban users that have these keywords in previous ban reasons.

            public string WatchlistGroup; // the group name that watched users should be added in
            public int WatchlistCeiling; // Auto add players with X amount of previous bans to a watchlist.

            public string BetterChatDirtyPlayerTag; // tag for players that are dirty.
            public bool BroadcastPlayerBanReport; // tag for players that are dirty.

            public bool BroadcastNewBans; // Broadcast to the entire server when true

            public string ServerName; // never change this, auto fetched
            public int ServerPort; // never change this, auto fetched
            public string ServerVersion; // never change this, auto fetched
            public bool ServerAdminShareDetails; // Default: false - indicates if you want your contact info to be visible to other server admins, and to users that have been auto banned. 
            public string ServerAdminName; // please fill in your main admins real name. This is to add a better trust level to your server.
            public string ServerAdminEmail; // please fill in your main admins email. This is to add a better trust level to your server.
            public string ServerApiKey; // for future reference, leave as is. 
        }

        #region Plugin Classes & Hooks Rust
#if RUST
        #region Arkan
        public class PlayersArkanViolationsData {
            public int seed;
            public int mapSize;
            public string serverTimeStamp;
            public DateTime lastSaveTime;
            public DateTime lastChangeTime;
            public Dictionary<ulong, PlayerArkanViolationsData> Players = new Dictionary<ulong, PlayerArkanViolationsData>();
        }

        public class PlayerArkanViolationsData {
            public ulong PlayerID;
            public string PlayerName;
            public SortedDictionary<string, NoRecoilViolationData> noRecoilViolations = new SortedDictionary<string, NoRecoilViolationData>();
            public SortedDictionary<string, AIMViolationData> AIMViolations = new SortedDictionary<string, AIMViolationData>();
            public SortedDictionary<string, InRockViolationsData> inRockViolations = new SortedDictionary<string, InRockViolationsData>();
        }

        public class HitData {
            public ProjectileRicochet hitData;
            public Vector3 startProjectilePosition;
            public Vector3 startProjectileVelocity;
            public Vector3 hitPositionWorld;
            public Vector3 hitPointStart;
            public Vector3 hitPointEnd;
            public bool isHitPointNearProjectileTrajectoryLastSegmentEndPoint = true;
            public bool isHitPointOnProjectileTrajectory = true;
            public bool isProjectileStartPointAtEndReverseProjectileTrajectory = true;
            public bool isHitPointNearProjectilePlane = true;
            public bool isLastSegmentOnProjectileTrajectoryPlane = true;
            public float distanceFromHitPointToProjectilePlane = 0f;
            public int side;
            public Vector3 pointProjectedOnLastSegmentLine;
            public float travelDistance = 0f;
            public float delta = 1f;
            public Vector3 lastSegmentPointStart;
            public Vector3 lastSegmentPointEnd;
            public Vector3 reverseLastSegmentPointStart;
            public Vector3 reverseLastSegmentPointEnd;
        }

        public class AIMViolationData {
            public int projectileID;
            public int violationID;
            public DateTime firedTime;
            public Vector3 startProjectilePosition;
            public Vector3 startProjectileVelocity;
            public string hitInfoInitiatorPlayerName;
            public string hitInfoInitiatorPlayerUserID;
            public string hitInfoHitEntityPlayerName;
            public string hitInfoHitEntityPlayerUserID;
            public string hitInfoBoneName;
            public Vector3 hitInfoHitPositionWorld;
            public float hitInfoProjectileDistance;
            public Vector3 hitInfoPointStart;
            public Vector3 hitInfoPointEnd;
            public float hitInfoProjectilePrefabGravityModifier;
            public float hitInfoProjectilePrefabDrag;
            public string weaponShortName;
            public string ammoShortName;
            public string bodyPart;
            public float damage;
            public bool isEqualFiredProjectileData = true;
            public bool isPlayerPositionToProjectileStartPositionDistanceViolation = false;
            public float distanceDifferenceViolation = 0f;
            public float calculatedTravelDistance;
            public bool isAttackerMount = false;
            public bool isTargetMount = false;
            public string attackerMountParentName;
            public string targetMountParentName;
            public float firedProjectileFiredTime;
            public float firedProjectileTravelTime;
            public Vector3 firedProjectilePosition;
            public Vector3 firedProjectileVelocity;
            public Vector3 firedProjectileInitialPosition;
            public Vector3 firedProjectileInitialVelocity;
            public Vector3 playerEyesLookAt;
            public Vector3 playerEyesPosition;
            public bool hasFiredProjectile = false;
            public List<HitData> hitsData = new List<HitData>();
            public float gravityModifier;
            public float drag;
            public float forgivenessModifier = 1f;
            public float physicsSteps = 32f;
            public List<string> attachments = new List<string>();
        }

        public struct ProjectileRicochet {
            public int projectileID;
            public Vector3 hitPosition;
            public Vector3 inVelocity;
            public Vector3 outVelocity;
        }

        public class NoRecoilViolationData {
            public int ShotsCnt;
            public int NRViolationsCnt;
            public float violationProbability;
            public bool isMounted;
            public Vector3 mountParentPosition;
            public Vector4 mountParentRotation;
            public List<string> attachments = new List<string>();
            public string ammoShortName;
            public string weaponShortName;

            public Dictionary<int, SuspiciousProjectileData> suspiciousNoRecoilShots = new Dictionary<int, SuspiciousProjectileData>();
        }

        public class InRockViolationsData {
            public DateTime dateTime;
            public Dictionary<int, InRockViolationData> inRockViolationsData = new Dictionary<int, InRockViolationData>();
        }

        public class InRockViolationData {
            public DateTime dateTime;
            public float physicsSteps;
            public float targetHitDistance;
            public string targetName;
            public string targetID;
            public float targetDamage;
            public string targetBodyPart;
            public Vector3 targetHitPosition;
            public Vector3 rockHitPosition;
            public FiredProjectile firedProjectile;
            public int projectileID;
            public float drag;
            public float gravityModifier;
        }

        public struct SuspiciousProjectileData {
            public DateTime timeStamp;
            public int projectile1ID;
            public int projectile2ID;
            public float timeInterval;
            public Vector3 projectile1Position;
            public Vector3 projectile2Position;
            public Vector3 projectile1Velocity;
            public Vector3 projectile2Velocity;
            public Vector3 closestPointLine1;
            public Vector3 closestPointLine2;
            public Vector3 prevIntersectionPoint;
            public float recoilAngle;
            public float recoilScreenDistance;
            public bool isNoRecoil;
            public bool isShootedInMotion;
        }

        public class FiredProjectile {
            public DateTime firedTime;
            public Vector3 projectileVelocity;
            public Vector3 projectilePosition;
            public Vector3 playerEyesLookAt;
            public Vector3 playerEyesPosition;
            public bool isChecked;
            public string ammoShortName;
            public string weaponShortName;
            public uint weaponUID;
            public bool isMounted;
            public string mountParentName;
            public Vector3 mountParentPosition;
            public Vector4 mountParentRotation;
            public List<ProjectileRicochet> hitsData = new List<ProjectileRicochet>();
            public List<string> attachments = new List<string>();
            public float NRProbabilityModifier = 1f;
        }


        private void API_ArkanOnNoRecoilViolation(BasePlayer player, int NRViolationsNum, string json) {
            if (json != null) {
                NoRecoilViolationData nrvd = JsonConvert.DeserializeObject<NoRecoilViolationData>(json);
                if (nrvd != null) {
                    server.Broadcast(GetMsg("Arkan No Recoil Violation", new Dictionary<string, string> {
                        ["player"] = player.displayName,
                        ["violationNr"] = NRViolationsNum.ToString(specifier, culture),
                        ["ammo"] = nrvd.ammoShortName,
                        ["shots"] = nrvd.ShotsCnt.ToString(specifier, culture),
                        ["weapon"] = nrvd.weaponShortName
                    }));
                    ISAPlayer isaPlayer = GetPlayerCache(player.UserIDString);
                    _playerData[player.UserIDString].AddArkanData(nrvd);
                }
            }
        }

        private void API_ArkanOnAimbotViolation(BasePlayer player, int AIMViolationsNum, string json) {
            if (json != null) {
                AIMViolationData aimvd = JsonConvert.DeserializeObject<AIMViolationData>(json);
                if (aimvd != null) {
                    server.Broadcast(GetMsg("Arkan Aimbot Violation", new Dictionary<string, string> {
                        ["player"] = player.displayName,
                        ["violationNr"] = AIMViolationsNum.ToString(specifier, culture),
                        ["ammo"] = aimvd.ammoShortName,
                        ["shots"] = aimvd.hitsData.Count.ToString(specifier, culture),
                        ["weapon"] = aimvd.weaponShortName
                    }));
                    ISAPlayer isaPlayer = GetPlayerCache(player.UserIDString);
                    _playerData[player.UserIDString].AddArkanData(aimvd);
                }
            }
        }

        private void API_ArkanOnInRockViolation(BasePlayer player, int IRViolationsNum, string json) {
            if (json != null) {
                InRockViolationsData irvd = JsonConvert.DeserializeObject<InRockViolationsData>(json);
                if (irvd != null) {
                    server.Broadcast(GetMsg("Arkan In Rock Violation", new Dictionary<string, string> {
                        ["player"] = player.displayName,
                        ["violationNr"] = IRViolationsNum.ToString(specifier, culture),
                        ["ammo"] = irvd.inRockViolationsData[1].firedProjectile.ammoShortName,
                        ["weapon"] = irvd.inRockViolationsData[1].firedProjectile.weaponShortName,
                        ["PlayerNotFound"] = $"{player} not found, if the usernmae contains a space or special character, then please use quotes around it."
                    }));
                    ISAPlayer isaPlayer = GetPlayerCache(player.UserIDString);
                    _playerData[player.UserIDString].AddArkanData(irvd);
                }
            }
        }

        #endregion
#endif
        #endregion
        #endregion
    }
}
