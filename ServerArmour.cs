using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
#if RUST || HURTWORLD || SEVENDAYSTODIE || REIGNOFKINGS || THEFOREST
using UnityEngine;
#endif
using Time = Oxide.Core.Libraries.Time;


namespace Oxide.Plugins {
    [Info("ServerArmour", "Pho3niX90", "0.0.81")]
    [Description("Protect your server! Auto ban known hacker, scripter and griefer accounts, and notify server owners of threats.")]
    class ServerArmour : CovalencePlugin {

        #region Variables
        Dictionary<string, ISAPlayer> _playerData = new Dictionary<string, ISAPlayer>();
        private Time _time = GetLibrary<Time>();
        private double cacheLifetime = 300; // minutes
        string[] groups;
        private ISAConfig config;
        string thisServerIp;
        string settingsVersion = "0.0.2";
        string specifier = "G";
        int secondsBetweenWebRequests;
        CultureInfo culture = CultureInfo.CreateSpecificCulture("en-US");
        StringComparison defaultCompare = StringComparison.InvariantCultureIgnoreCase;
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
            LoadData();
            Puts("Server Armour is being initialized.");
            config = Config.ReadObject<ISAConfig>();

            if (!config.Version.Equals(settingsVersion)) UpgradeConfig(config.Version, settingsVersion);
            thisServerIp = server.Address.ToString();

            if (config.ServerVersion.Equals(server.Version, defaultCompare)) {
                config.ServerName = server.Name;
                config.ServerPort = server.Port;
                config.ServerVersion = server.Version;
                SaveConfig();
                RegisterTag();
            }

            LoadDefaultMessages();
            CheckGroups();
            permission.RegisterPermission(PermissionToBan, this);
        }

        void OnServerInitialized() {
            RegisterTag();
        }

        void Loaded() {

#if RUST
            timer.Once((ServerMgr.Instance != null) ? 10 : 300, () => {
                ServerMgr.Instance.StartCoroutine(CheckOnlineUsers());
                ServerMgr.Instance.StartCoroutine(CheckLocalBans());
            });
#else
            CheckOnlineUsers();
            CheckLocalBans();
#endif
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
            GetPlayerBans(player, true);
        }

        void OnUserDisconnected(IPlayer player) {
            Puts($"{player.Name} ({player.Id}) disconnected");
        }

        bool CanUserLogin(string name, string id, string ip) {
            bool canLogin = !AssignGroupsAndBan(players.FindPlayer(name));
            if (!canLogin) {
                Puts($"{ip}:{id}:{name} tried to connect. But the connection was rejected due to him being banned");
            }
            return canLogin;
        }

        void OnUserKicked(IPlayer player, string reason) {
            Puts($"Player {player.Name} ({player.Id}) was kicked, reason: {reason}");
        }

        void OnUserApproved(string name, string id, string ip) {
            Puts($"{name} ({id}) at {ip} has been approved to connect");
        }

        /* Unused atm, will uncomment as needed

                void OnUserNameUpdated(string id, string oldName, string newName) {
                    Puts($"Player name changed from {oldName} to {newName} for ID {id}");
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
        void GetPlayerBans(IPlayer player, bool reCache = false) {
            bool isCached = IsPlayerCached(player.Id);
            uint currentTimestamp = _time.GetUnixTimestamp();
            bool reportSent = false;

            if (isCached) {
                GetPlayerCache(player.Id).lastConnected = currentTimestamp;
                double minutesOld = Math.Round((currentTimestamp - GetPlayerCache(player.Id).cacheTimestamp) / 60.0);
                bool oldCache = minutesOld >= cacheLifetime;
                LogDebug($"Player {player.Name}'s cache is {minutesOld} minutes old. " + ((oldCache) ? "Cache is old" : "Cache is fresh"));
                if (oldCache || reCache) {
                    DeletePlayerCache(player.Id);
                    LogDebug($"Will now update local cache for player {player.Name}");
                    isCached = false;
                    reportSent = true;
                } else {
                    GetPlayerReport(player, player.IsConnected);
                    return; //user already cached, therefore do not check again before cache time laps.
                }
            } else {
                LogDebug($"Player {player.Name} not cached");
            }


            string playerName = Uri.EscapeDataString(player.Name);
            string url = $"https://io.serverarmour.com/checkUser?steamid={player.Id}&username={playerName}&ip={player.Address}" + ServerGetString();
            webrequest.Enqueue(url, null, (code, response) => {
                if (code != 200 || response == null) {
                    Puts($"Couldn't get an answer from ServerArmour.com! Error: {code} {response}");
                    return;
                }
                ISAPlayer isaPlayer = JsonConvert.DeserializeObject<ISAPlayer>(response);
                isaPlayer.cacheTimestamp = _time.GetUnixTimestamp();
                isaPlayer.lastConnected = _time.GetUnixTimestamp();

                if (config.AutoKick_BadIp && isaPlayer.ipRating > 0.98) {
                    player.Kick(GetMsg("Reason: Bad IP"));
                }

                if (!IsPlayerCached(isaPlayer.steamid)) {
                    AddPlayerCached(isaPlayer);
                } else {
                    SetPlayerBanData(isaPlayer);
                }
                if (!reportSent) {
                    GetPlayerReport(isaPlayer, player.IsConnected);
                    reportSent = false;
                }


            }, this, RequestMethod.GET);
        }

        void AddBan(IPlayer player, string banreason) {
            string dateTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
            string url = $"https://io.serverarmour.com/addBan?steamid={player.Id}&username={player.Name}&ip={player.Address}&reason={banreason}&dateTime={dateTime}" + ServerGetString();
            webrequest.Enqueue(url, null, (code, response) => {
                if (code != 200 || response == null) {
                    Puts($"Couldn't get an answer from ServerArmour.com!");
                    return;
                }

                if (IsPlayerCached(player.Id)) {
                    Puts($"{player.Name} has ban cached, now updating.");
                    AddPlayerBanData(player, new ISABan { serverName = server.Name, date = dateTime, reason = banreason, serverIp = thisServerIp });
                } else {
                    Puts($"{player.Name} had no ban data cached, now creating.");
                    AddPlayerCached(player,
                        new ISAPlayer {
                            steamid = player.Id,
                            username = player.Name,
                            serverBanCount = 1,
                            cacheTimestamp = _time.GetUnixTimestamp(),
                            lastConnected = _time.GetUnixTimestamp(),
                            serverBanData = new List<ISABan> { new ISABan { serverName = server.Name, date = dateTime, reason = banreason, serverIp = thisServerIp } }
                        });
                }
                //SaveData();
            }, this, RequestMethod.GET);
        }

        ISABan IsBanned(string steamid) {
            if (IsPlayerCached(steamid)) {
                ISAPlayer isaPlayer = GetPlayerCache(steamid);
                foreach (ISABan ban in isaPlayer.serverBanData) {
                    if (ban.serverIp.Equals(thisServerIp, defaultCompare)) {
                        return ban;
                    }
                }
            }
            return null;
        }

        string GetBanReason(ISAPlayer isaPlayer) {
            foreach (ISABan ban in isaPlayer.serverBanData) {
                if (ban.serverIp.Equals(thisServerIp, defaultCompare)) {
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
            if (!HasPermission(player, PermissionToBan)) {
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

                string msg = GetMsg("Player Now Banned", new Dictionary<string, string> { ["player"] = iPlayer.Name, ["reason"] = args[1] });
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
                GetPlayerBans(playerToCheck, forceUpdate);
            }

            CheckLocalBans();
            GetPlayerReport(playerToCheck, player);
        }
        #endregion

        #region VPN/Proxy
        #endregion

        #region Ban System
        bool BanPlayer(IPlayer iPlayer, ISABan ban) {
            AddBan(iPlayer, ban.reason);
            iPlayer.Kick(ban.reason);
            return true;
        }
        #endregion

        #region IEnumerators

#if RUST || HURTWORLD || SEVENDAYSTODIE || THEFOREST
        System.Collections.IEnumerator CheckOnlineUsers() {
            var waitTime = 0.2f;
            IEnumerable<IPlayer> allPlayers = players.Connected;
            Puts("Will now inspect all online users, time etimation: " + (allPlayers.Count() * waitTime) + " seconds");
            for (var i = 1; i < allPlayers.Count(); i++) {
                Puts($"Inpecting online user {i + 1} of {allPlayers.Count()} for infractions");
                IPlayer player = allPlayers.ElementAt(i);
                if (player != null) {
                    GetPlayerBans(player, true);
                }
                yield return new WaitForSecondsRealtime(waitTime);

            }
            Puts("Inspection completed.");
        }
#else

        void CheckOnlineUsers() {
            IEnumerable<IPlayer> allPlayers = players.Connected;
            for (var i = 1; i < allPlayers.Count(); i++) {
                Puts($"Inpecting online user {i + 1} of {allPlayers.Count()} for infractions");
                IPlayer player = allPlayers.ElementAt(i);
                if (player != null) {
                    GetPlayerBans(player, true);
                }

            }
            Puts("Checking completed.");
        }
#endif

        private System.Collections.IEnumerator CheckLocalBans() {
#if RUST
            IEnumerable<ServerUsers.User> bannedUsers = ServerUsers.GetAll(ServerUsers.UserGroup.Banned);

            for (var i = 0; i < bannedUsers.Count(); i++) {
                ServerUsers.User usr = bannedUsers.ElementAt(i);

                Puts($"Checking local user ban {i + 1} of {bannedUsers.Count()}");

                bool containsMyBan = false;
                if (IsPlayerCached(usr.steamid.ToString(specifier, culture))) {
                    List<ISABan> bans = GetPlayerBanData(usr.steamid.ToString(specifier, culture));
                    foreach (ISABan ban in bans) {
                        if (ban.serverIp.Equals(thisServerIp, defaultCompare)) {
                            containsMyBan = true;
                            break;
                        }
                    }
                }
                if (!containsMyBan) {
                    IPlayer player = covalence.Players.FindPlayer(usr.steamid.ToString(specifier, culture));
                    AddBan(player, usr.notes);
                }
                yield return new WaitForSecondsRealtime(1f);
            }
#else
            return null;
#endif
        }
        #endregion

        #region Data Handling
        bool IsPlayerDirty(string steamid) {
            ISAPlayer isaPlayer = GetPlayerCache(steamid);
            return IsPlayerCached(steamid) && (isaPlayer.serverBanCount > 0 || isaPlayer.steamData.CommunityBanned > 0 || isaPlayer.steamData.NumberOfGameBans > 0 || isaPlayer.steamData.VACBanned > 0);
        }

        bool IsPlayerCached(string steamid) => _playerData.ContainsKey(steamid);
        bool DeletePlayerCache(string steamid) { bool res = _playerData.Remove(steamid); SaveData(); return res; }
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
                //Puts(report);
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
            string watchlistGroup = GetConfig("WatchlistGroup", "serverarmour.watchlists");

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

        bool AssignGroupsAndBan(IPlayer player) {
            try {
                ISAPlayer isaPlayer = IsPlayerCached(player.Id) ? GetPlayerCache(player.Id) : null;
                if (isaPlayer == null) return false;

                if (config.AutoBanOn && ShouldBan(isaPlayer)) return ShouldBan(isaPlayer);


                if (config.WatchlistCeiling <= isaPlayer.serverBanCount) {

                }
                ISABan ban = IsBanned(isaPlayer.steamid);
                bool isBanned = ban != null;
                if (isBanned) {
                    player.Kick(ban.reason);
                    server.Broadcast($"{GetChatTag()} {isaPlayer.username} wasn't allowed to connect\nReason: {GetBanReason(isaPlayer)}");
                    return true;
                }
            } catch (NullReferenceException nre) {
                return false;
            }
            return false;
        }

        bool ShouldBan(ISAPlayer isaPlayer) {
            bool ceilingBan = config.AutoBanOn && (config.AutoVacBanCeiling <= isaPlayer.steamData.NumberOfVACBans || config.AutoBanCeiling < isaPlayer.serverBanCount);
            bool keywordBan = false;
            bool keywordBanCheck = config.AutoBanOn && (config.AutoBan_Reason_Keyword_Aimbot || config.AutoBan_Reason_Keyword_Cheat || config.AutoBan_Reason_Keyword_EspHack || config.AutoBan_Reason_Keyword_Hack || config.AutoBan_Reason_Keyword_Insult || config.AutoBan_Reason_Keyword_Ping || config.AutoBan_Reason_Keyword_Racism || config.AutoBan_Reason_Keyword_Script || config.AutoBan_Reason_Keyword_Toxic);
            if (keywordBanCheck) {
                foreach (ISABan ban in isaPlayer.serverBanData) {
                    if (config.AutoBan_Reason_Keyword_Aimbot && ban.isAimbot) keywordBan = true;
                    if (config.AutoBan_Reason_Keyword_Cheat && ban.isCheat) keywordBan = true;
                    if (config.AutoBan_Reason_Keyword_EspHack && ban.isEspHack) keywordBan = true;
                    if (config.AutoBan_Reason_Keyword_Hack && ban.isHack) keywordBan = true;
                    if (config.AutoBan_Reason_Keyword_Insult && ban.isInsult) keywordBan = true;
                    if (config.AutoBan_Reason_Keyword_Ping && ban.isPing) keywordBan = true;
                    if (config.AutoBan_Reason_Keyword_Racism && ban.isRacism) keywordBan = true;
                    if (config.AutoBan_Reason_Keyword_Script && ban.isScript) keywordBan = true;
                    if (config.AutoBan_Reason_Keyword_Toxic && ban.isToxic) keywordBan = true;

                }
            }

            return ceilingBan || keywordBan;
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
                ["Protected MSG"] = "Server protected by <color=#008080ff>ServerArmour</color>",
                ["User Dirty MSG"] = "<color=#008080ff>Server Armour Report:\n {steamid}:{username}</color> is {status}.\n <color=#ff0000ff>Server Bans:</color> {serverBanCount}\n <color=#ff0000ff>Game Bans:</color> {NumberOfGameBans}\n <color=#ff0000ff>Vac Bans:</color> {NumberOfVACBans}\n <color=#ff0000ff>Economy Status:</color> {EconomyBan}",
                ["Command sa.cp Error"] = "Wrong format, example: /sa.cp usernameORsteamid trueORfalse",
                ["Arkan No Recoil Violation"] = "<color=#ff0000>{player}</color> received an Arkan no recoil violation.\n<color=#ff0000>Violation</color> #{violationNr}, <color=#ff0000>Weapon:</color> {weapon}, <color=#ff0000>Ammo:</color> {ammo}, <color=#ff0000>Shots count:</color> {shots}\n Admins will investigate ASAP, please have handcams ready.\n This might be a false-positive, but all violations need to be investigated.",
                ["Arkan Aimbot Violation"] = "<color=#ff0000>{player}</color> received an Arkan aimbot violation.\n<color=#ff0000>Violation</color>  #{violationNr}, <color=#ff0000>Weapon:</color> {weapon}, <color=#ff0000>Ammo:</color> {ammo}\n Admins will investigate ASAP, please have handcams ready.\n This might be a false-positive, but all violations need to be investigated.",
                ["Arkan In Rock Violation"] = "<color=#ff0000>{player}</color> received an Arkan in rock violation.\n<color=#ff0000>Violation</color>  #{violationNr}, <color=#ff0000>Weapon:</color> {weapon}, <color=#ff0000>Ammo:</color> {ammo}\n Admins will investigate ASAP, please have handcams ready.\n This might be a false-positive, but all violations need to be investigated.",
                ["Player Now Banned"] = "<color=#ff0000>{player}</color> has been banned\n<color=#ff0000>Reason: </color> {reason}",
                ["Reason: Bad IP"] = "Bad IP Detected, either due to a VPN/Proxy"
            }, this);
        }

        bool HasPermission(IPlayer player, string permissionName) {
            if (player.IsAdmin) return true;
            return permission.UserHasPermission(player.Id, permissionName);
        }
        #endregion

        #region Plugins methods
        string GetChatTag() => "<color=#008080ff>[Server Armour]: </color>";
        void RegisterTag() {
            if (BetterChat != null)
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
            public double ipRating;

            public ISAPlayer CreatePlayer(IPlayer bPlayer) {
                steamid = bPlayer.Id;
                username = bPlayer.Name;
                cacheTimestamp = new Time().GetUnixTimestamp();
                lastConnected = new Time().GetUnixTimestamp();
                steamData = new ISASteamData();
                serverBanData = new List<ISABan>();
                return this;
            }
        }

        public class ISABan {
            public string banId;
            public string serverName;
            public string serverIp;
            public string reason;
            public string date;
            public bool isAimbot;
            public bool isHack;
            public bool isEspHack;
            public bool isScript;
            public bool isCheat;
            public bool isToxic;
            public bool isInsult;
            public bool isPing;
            public bool isRacism;
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

            public bool AutoBan_Reason_Keyword_Aimbot;
            public bool AutoBan_Reason_Keyword_Hack;
            public bool AutoBan_Reason_Keyword_EspHack;
            public bool AutoBan_Reason_Keyword_Script;
            public bool AutoBan_Reason_Keyword_Cheat;
            public bool AutoBan_Reason_Keyword_Toxic;
            public bool AutoBan_Reason_Keyword_Insult;
            public bool AutoBan_Reason_Keyword_Ping;
            public bool AutoBan_Reason_Keyword_Racism;

            public bool AutoKick_BadIp;
        }

        #region Plugin Classes & Hooks Rust
#if RUST
        #region Arkan

        private void API_ArkanOnNoRecoilViolation(BasePlayer player, int NRViolationsNum, string json) {
            if (json != null) {
                Puts("Arkan: " + json);
                /*NoRecoilViolationData nrvd = JsonConvert.DeserializeObject<NoRecoilViolationData>(json);
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
                }*/
            }
        }

        private void API_ArkanOnAimbotViolation(BasePlayer player, int AIMViolationsNum, string json) {
            if (json != null) {
                Puts("Arkan: " + json);
                /*AIMViolationData aimvd = JsonConvert.DeserializeObject<AIMViolationData>(json);
                if (aimvd != null) {
                    server.Broadcast(GetMsg("Arkan Aimbot Violation", new Dictionary<string, string> {
                        ["player"] = player.displayName,
                        ["violationNr"] = AIMViolationsNum.ToString(specifier, culture),
                        ["ammo"] = aimvd.ammoShortName,
                        ["weapon"] = aimvd.weaponShortName
                    }));
                    ISAPlayer isaPlayer = GetPlayerCache(player.UserIDString);
                    _playerData[player.UserIDString].AddArkanData(aimvd);
                }*/
            }
        }

        private void API_ArkanOnInRockViolation(BasePlayer player, int IRViolationsNum, string json) {
            if (json != null) {
                Puts("Arkan: " + json);
                /*InRockViolationsData irvd = JsonConvert.DeserializeObject<InRockViolationsData>(json);
                if (irvd != null) {
                    Puts("Arkan: " + json);
                    server.Broadcast(GetMsg("Arkan In Rock Violation", new Dictionary<string, string> {
                        ["player"] = player.displayName,
                        ["violationNr"] = IRViolationsNum.ToString(specifier, culture),
                        ["ammo"] = irvd.inRockViolationsData[1].firedProjectile.ammoShortName,
                        ["weapon"] = irvd.inRockViolationsData[1].firedProjectile.weaponShortName,
                        ["PlayerNotFound"] = $"{player} not found, if the usernmae contains a space or special character, then please use quotes around it."
                    }));
                    ISAPlayer isaPlayer = GetPlayerCache(player.UserIDString);
                    _playerData[player.UserIDString].AddArkanData(irvd);
                }*/
            }
        }

        #endregion
#endif
        #endregion
        #endregion
    }
}
