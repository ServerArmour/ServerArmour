using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using UnityEngine;
using Time = Oxide.Core.Libraries.Time;

namespace Oxide.Plugins {
    [Info("ServerArmour", "Pho3niX90", "0.0.6")]
    [Description("Protect your server! Auto ban known hacker, scripter and griefer acounts, and notify other server owners of threats.")]
    class ServerArmour : CovalencePlugin {

        #region Variables
        Dictionary<string, ISAPlayer> _playerData = new Dictionary<string, ISAPlayer>();
        private Time _time = GetLibrary<Time>();
        private double cacheLifetime = 300; // minutes
        string[] groups;
        private ISAConfig config;
        static string thisServerIp;
        char[] ipChrArray = new char[] { '.' };
        #region Permissions
        const string PermissionBan = "serverarmour.ban";
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
            thisServerIp = server.Address.ToString();

            if (config.ServerVersion.Equals(server.Version.ToString())) {
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

        void OnPluginLoaded(Plugin plugin) {
            if (plugin.Title == "BetterChat") RegisterTag();
        }
        #endregion

        #region WebRequests
        void GetPlayerBans(IPlayer player, bool onlyGetUncached = false, bool connectedTime = true) {

            if (PlayerCached(player.Id) && !onlyGetUncached) {
                uint cachedTimestamp = PlayerGetCache(player.Id).cacheTimestamp;
                uint currentTimestamp = _time.GetUnixTimestamp();
                double minutesOld = (currentTimestamp - cachedTimestamp) / 60.0 / 1000.0;
                bool oldCache = cacheLifetime <= minutesOld;
                PlayerGetCache(player).lastConnected = _time.GetUnixTimestamp();
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
                if (!PlayerCached(isaPlayer.steamid)) {
                    PlayerAddCached(isaPlayer);
                } else {
                    PlayerSetBanDataCache(isaPlayer);
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

                if (PlayerCached(player.Id.ToString())) {
                    LogDebug("Ban cached, now updating.");
                    PlayerAddBanDataCache(player, new ISABan { serverName = server.Name, date = dateTime, reason = banreason, serverIp = thisServerIp });
                } else {
                    LogDebug("Ban not cached, now creating.");
                    PlayerAddCached(player,
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
            if (PlayerCached(steamid)) {
                ISAPlayer isaPlayer = PlayerGetCache(steamid);
                foreach (ISABan ban in isaPlayer.serverBanData) {
                    if (ban.serverIp.Equals(thisServerIp)) {
                        return true;
                    }
                }
            }
            return false;
        }

        string GetBanReason(ISAPlayer isaPlayer) {
            foreach (ISABan ban in isaPlayer.serverBanData) {
                if (ban.serverIp.Equals(thisServerIp)) {
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
            if (!hasPermission(player, PermissionBan)) {
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

            if (!PlayerCached(iPlayer.Id)) {
                isaPlayer = new ISAPlayer().CreatePlayer(iPlayer);
                PlayerAddCached(isaPlayer);
            } else {
                isaPlayer = PlayerGetCache(args[0]);
            }

            if (BanPlayer(iPlayer,
                new ISABan {
                    serverName = server.Name,
                    serverIp = thisServerIp,
                    reason = args[1],
                    date = new Time().GetDateTimeFromUnix(new Time().GetUnixTimestamp()).ToShortDateString()
                })) {
                player.Reply(GetMsg("PlayerNowBanned"));
            }
        }

        [Command("sa.cp")]
        void SCmdCheckPlayer(IPlayer player, string command, string[] args) {
            //if (args.Length == 0) player.Reply(GetMsg("Command sa.cp Error"));
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

            if (PlayerCached(playerToCheck.Id) && forceUpdate) {
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
        bool PlayerIsDirty(string steamid) => PlayerCached(steamid) && (PlayerGetCache(steamid).serverBanCount > 0 || PlayerGetCache(steamid).steamData.CommunityBanned > 0 || PlayerGetCache(steamid).steamData.NumberOfGameBans > 0 || PlayerGetCache(steamid).steamData.VACBanned > 0);
        bool PlayerCached(string steamid) => _playerData.ContainsKey(steamid);
        bool PlayerDeleteCache(string steamid) => _playerData.Remove(steamid);
        void PlayerAddCached(ISAPlayer isaplayer) => _playerData.Add(isaplayer.steamid, isaplayer);
        void PlayerAddCached(IPlayer iplayer, ISAPlayer isaplayer) => _playerData.Add(iplayer.Id, isaplayer);
        ISAPlayer PlayerGetCache(string steamid) => PlayerCached(steamid) ? _playerData[steamid] : null;
        ISAPlayer PlayerGetCache(IPlayer player) => PlayerCached(player.Id) ? _playerData[player.Id] : null;
        List<ISABan> PlayerGetBanDataCache(string steamid) => _playerData[steamid].serverBanData;
        int PlayerGetBanDataCountCache(string steamid) => _playerData[steamid].serverBanData.Count;
        void PlayerSetBanDataCache(ISAPlayer isaplayer) => _playerData[isaplayer.steamid].serverBanData = isaplayer.serverBanData;
        void PlayerAddBanDataCache(IPlayer iplayer, ISABan isaban) => _playerData[iplayer.Id].serverBanData.Add(isaban);

        void GetPlayerReport(IPlayer player) {
            ISAPlayer isaPlayer = PlayerGetCache(player);
            if (isaPlayer != null)
                GetPlayerReport(isaPlayer, player.IsConnected);
        }

        void GetPlayerReport(IPlayer player, IPlayer cmdplayer) {
            ISAPlayer isaPlayer = PlayerGetCache(player);
            if (isaPlayer != null)
                GetPlayerReport(isaPlayer, player.IsConnected, true, cmdplayer);
        }

        void GetPlayerReport(IPlayer player, bool isCommand = false) {
            ISAPlayer isaPlayer = PlayerGetCache(player);
            if (isaPlayer != null)
                GetPlayerReport(isaPlayer, player.IsConnected, isCommand);
        }

        string GetPlayerId(string identifier) => players.FindPlayer(identifier).Id;

        void GetPlayerReport(ISAPlayer isaPlayer, bool isConnected = true, bool isCommand = false, IPlayer cmdPlayer = null) {

            if (PlayerIsDirty(isaPlayer.steamid) || isCommand) {
                string report = GetMsg("User Dirty MSG",
                        new Dictionary<string, string> {
                            ["status"] = PlayerIsDirty(isaPlayer.steamid) ? "dirty" : "clean",
                            ["steamid"] = isaPlayer.steamid,
                            ["username"] = isaPlayer.username,
                            ["serverBanCount"] = isaPlayer.serverBanCount.ToString(),
                            ["NumberOfGameBans"] = isaPlayer.steamData.NumberOfGameBans.ToString(),
                            ["NumberOfVACBans"] = isaPlayer.steamData.NumberOfVACBans.ToString(),
                            ["EconomyBan"] = isaPlayer.steamData.EconomyBan.ToString()
                        });
                if (config.BroadcastPlayerBanReport && isConnected && !isCommand) {
                    server.Broadcast(report.Replace(isaPlayer.steamid + ":", "").Replace(isaPlayer.steamid, ""));
                }
                if (isCommand) {
                    cmdPlayer.Reply(report.Replace(isaPlayer.steamid + ":", "").Replace(isaPlayer.steamid, ""));
                }
                Puts(report);
            }
        }

        T GetConfig<T>(string name, T defaultValue) => Config[name] == null ? defaultValue : (T)Convert.ChangeType(Config[name], typeof(T));
        protected override void LoadDefaultConfig() {
            LogWarning("Creating a new configuration file");
            Config.WriteObject(
                new ISAConfig {
                    Debug = false,
                    ShowProtectedMsg = true,
                    BetterChatDirtyPlayerTag = "DIRTY",
                    BroadcastPlayerBanReport = true,
                    AutoBanGroup = "serverarmour.bans",
                    AutoBanOn = true,
                    AutoBanCeiling = 5,
                    AutoVacBanCeiling = 2,
                    AutoBanReasonKeywords = new string[] { "aimbot", "esp" },

                    WatchlistGroup = "serverarmour.watchlist",
                    WatchlistCeiling = 1,

                    ServerName = server.Name,
                    ServerPort = server.Port,
                    ServerVersion = server.Version,
                    ServerAdminName = "",
                    ServerAdminEmail = "",
                    ServerApiKey = "FREE"
                }, true);
            SaveConfig();
        }

        private void LogDebug(string txt) {
            if (config.Debug) Puts(txt);
        }

        void SaveData() {
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
            string[] strArray = arg.Split(ipChrArray);
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


                if (PlayerCached(usr.steamid.ToString())) {
                    foreach (ISABan ban in PlayerGetBanDataCache(usr.steamid.ToString())) {
                        if (ban.serverIp.Equals(thisServerIp)) {
                            containsMyBan = true;
                            break;
                        }
                    }
                }


            if (!containsMyBan) {
                    IPlayer player = covalence.Players.FindPlayer(usr.steamid.ToString());
                    AddBan(player, usr.notes);
                }
            }
#endif
        }


        bool AssignGroupsAndBan(IPlayer player) {
            try {
                ISAPlayer isaPlayer = PlayerCached(player.Id) ? PlayerGetCache(player.Id) : null;
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
            } catch (Exception e) {
                return false;
            }
            return false;
        }

        bool ShouldBan(ISAPlayer isaPlayer) {
            return config.AutoBanOn && (config.AutoVacBanCeiling <= isaPlayer.steamData.NumberOfVACBans || config.AutoBanCeiling < isaPlayer.serverBanCount);
        }
        #endregion

        #region API Hooks
        private int API_GetServerBanCount(string steamid) => PlayerCached(steamid) ? PlayerGetBanDataCountCache(steamid) : 0;
        private bool API_GetIsVacBanned(string steamid) => PlayerCached(steamid) ? PlayerGetCache(steamid).steamData.VACBanned == 1 : false;
        private bool API_GetIsCommunityBanned(string steamid) => PlayerCached(steamid) ? PlayerGetCache(steamid).steamData.CommunityBanned == 1 : false;
        private int API_GetVacBanCount(string steamid) => PlayerCached(steamid) ? PlayerGetCache(steamid).steamData.NumberOfVACBans : 0;
        private int API_GetGameBanCount(string steamid) => PlayerCached(steamid) ? PlayerGetCache(steamid).steamData.NumberOfGameBans : 0;
        private string API_GetEconomyBanStatus(string steamid) => PlayerCached(steamid) ? PlayerGetCache(steamid).steamData.EconomyBan : "none";
        private bool API_GetIsPlayerDirty(string steamid) => PlayerCached(steamid) && PlayerGetBanDataCountCache(steamid) > 0 || PlayerGetCache(steamid).steamData.VACBanned == 1;
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
                ["Arkan In Rock Violation"] = "<color=#ff0000>{player}</color> received an Arkan in rock violation.\n<color=#ff0000>Violation</color>  #{violationNr}, <color=#ff0000>Weapon:</color> {weapon}, <color=#ff0000>Ammo:</color> {ammo}\n Admins will investigate ASAP, please have handcams ready.\n This might be a false-positive, but all violations need to be investigated."
            }, this);
        }

        bool hasPermission(IPlayer player, string permissionName) {
            if (player.IsAdmin) return true;
            return permission.UserHasPermission(player.Id.ToString(), permissionName);
        }
        #endregion

        #region Plugins methods
        string GetChatTag() => "<color=#008080ff>[Server Armour]: </color>";
        void RegisterTag() {
            BetterChat?.Call("API_RegisterThirdPartyTitle", new object[] { this, new Func<IPlayer, string>(GetTag) });
        }

        string GetTag(IPlayer player) {
            if (BetterChat != null && PlayerIsDirty(player.Id) && config.BetterChatDirtyPlayerTag != "") {
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
            public bool Debug;
            public bool ShowProtectedMsg;
            public string AutoBanGroup; // the group name that banned users should be added in
            public bool AutoBanOn; // turn auto banning on or off. 
            public int AutoBanCeiling; // Auto ban players with X amount of previous bans.
            public int AutoVacBanCeiling; //  Auto ban players with X amount of vac bans.
            public string[] AutoBanReasonKeywords; // auto ban users that have these keywords in previous ban reasons.

            public string WatchlistGroup; // the group name that watched users should be added in
            public int WatchlistCeiling; // Auto add players with X amount of previous bans to a watchlist.

            public string BetterChatDirtyPlayerTag; // tag for players that are dirty.
            public bool BroadcastPlayerBanReport; // tag for players that are dirty.

            public string ServerName; // never change this, auto fetched
            public int ServerPort; // never change this, auto fetched
            public string ServerVersion; // never change this, auto fetched
            public string ServerAdminShareDetails; // Default: false - indicates if you want your contact info to be visible to other server admins, and to users that have been auto banned. 
            public string ServerAdminName; // please fill in your main admins real name. This is to add a better trust level to your server.
            public string ServerAdminEmail; // please fill in your main admins email. This is to add a better trust level to your server.
            public string ServerApiKey; // for future reference, leave as is. 
        }

        #region Plugin Classes & Hooks Rust
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

        #if RUST
        private void API_ArkanOnNoRecoilViolation(BasePlayer player, int NRViolationsNum, string json) {
            if (json != null) {
                NoRecoilViolationData nrvd = JsonConvert.DeserializeObject<NoRecoilViolationData>(json);
                if (nrvd != null) {
                    server.Broadcast(GetMsg("Arkan No Recoil Violation", new Dictionary<string, string> {
                        ["player"] = player.displayName,
                        ["violationNr"] = NRViolationsNum.ToString(),
                        ["ammo"] = nrvd.ammoShortName,
                        ["shots"] = nrvd.ShotsCnt.ToString(),
                        ["weapon"] = nrvd.weaponShortName
                    }));
                    ISAPlayer isaPlayer = PlayerGetCache(player.UserIDString);
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
                        ["violationNr"] = AIMViolationsNum.ToString(),
                        ["ammo"] = aimvd.ammoShortName,
                        ["shots"] = aimvd.hitsData.Count.ToString(),
                        ["weapon"] = aimvd.weaponShortName
                    }));
                    ISAPlayer isaPlayer = PlayerGetCache(player.UserIDString);
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
                        ["violationNr"] = IRViolationsNum.ToString(),
                        ["ammo"] = irvd.inRockViolationsData[1].firedProjectile.ammoShortName,
                        ["weapon"] = irvd.inRockViolationsData[1].firedProjectile.weaponShortName,
                        ["PlayerNotFound"] = $"{player} not found, if the usernmae contains a space or special character, then please use quotes around it."
                    }));
                    ISAPlayer isaPlayer = PlayerGetCache(player.UserIDString);
                    _playerData[player.UserIDString].AddArkanData(irvd);
                }
            }
        }
        #endif
        #endregion
        #endregion
        #endregion
    }
}
