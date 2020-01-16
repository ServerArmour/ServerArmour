using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using System;
using System.Collections.Generic;

namespace Oxide.Plugins {
    [Info("ServerArmour", "Pho3niX90", "0.0.2")]
    [Description("Protect your server! Auto ban known hacker, scripter and griefer acounts, and notify other server owners of threats.")]
    class ServerArmour : CovalencePlugin {

        #region Variables
        Dictionary<string, ISAPlayer> _playerData = new Dictionary<string, ISAPlayer>();
        private Time _time = GetLibrary<Time>();
        private double cacheLifetime = 300; // minutes
        string[] groups;
        private ISAConfig config;
        static string thisServerIp;
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
            }

            LoadDefaultMessages();

            CheckGroups();

            LoadData();
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

            timer.Once(30f, () => {
                CheckLocalBans();
            });

            Puts("Checking " + playerTotal + " players");
            Puts("Server Armour finished initializing.");
        }

        void Unload() {
            Puts("Server Armour unloading, will now save all data.");
            SaveConfig();
            SaveData();
            Puts("Server Armour finished unloaded.");
        }

        void OnUserConnected(IPlayer player) {
            GetPlayerBans(player);

            Puts($"{player.Name} ({player.Id}) connected from {player.Address}");

            if (player.IsAdmin) {
                Puts($"{player.Name} ({player.Id}) is admin");
            }

            // CheckIfBanned(player);
        }

        void OnUserDisconnected(IPlayer player) {
            Puts($"{player.Name} ({player.Id}) disconnected");
        }

        bool CanUserLogin(string name, string id, string ip) {
            if (name.ToLower().Contains("admin")) {
                Puts($"{name} ({id}) at {ip} tried to connect with 'admin' in name");
                return false;
            }

            return true;
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

        #endregion

        #region WebRequests
        void GetPlayerBans(IPlayer player, bool onlyGetUncached = false, bool connectedTime = true, bool forceRefresh = false) {


            if (PlayerCached(player.Id) && !onlyGetUncached) {
                uint cachedTimestamp = PlayerGetCache(player.Id).cacheTimestamp;
                uint currentTimestamp = _time.GetUnixTimestamp();
                double minutesOld = (currentTimestamp - cachedTimestamp) / 60.0 / 1000.0;
                bool oldCache = cacheLifetime <= minutesOld;
                LogDebug("Player " + player.Name + " exists, " + (oldCache ? "however his cache is old and will now refresh." : "and his cache is still fresh."));

                PlayerGetCache(player).lastConnected = _time.GetUnixTimestamp();
                if (!oldCache)
                    return; //user already cached, therefore do not check again before cache time laps.
            }


            string url = $"http://io.serverarmour.com/checkUser?steamid={player.Id}&username={player.Name}&ip={player.Address}" + ServerGetString();
            Puts(url);
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

                if (isaPlayer != null && isaPlayer.banCount > 0) {
                    Puts($"ServerArmour: {isaPlayer.steamid}:{isaPlayer.username} checked, and user is dirty with a total of *{isaPlayer.banCount}* bans");
                } else if (isaPlayer != null) {
                }

                SaveData();

            }, this, RequestMethod.GET);
        }

        void AddBan(IPlayer player, string banreason) {
            Puts("Running AddBan");
            string dateTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm").ToString();
            string url = $"http://io.serverarmour.com/addBan?steamid={player.Id}&username={player.Name}&ip={player.Address}&reason={banreason}&dateTime={dateTime}" + ServerGetString();
            Puts(url);
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
                            banCount = 1,
                            cacheTimestamp = _time.GetUnixTimestamp(),
                            lastConnected = _time.GetUnixTimestamp(),
                            banData = new List<ISABan> { new ISABan { serverName = server.Name, date = dateTime, reason = banreason, serverIp = thisServerIp } }
                        });
                }
                SaveData();
            }, this, RequestMethod.GET);
        }

        #endregion

        #region Commands
        [Command("serverarmour.checklocalbans")]
        void SCmdCheckLocalBans(IPlayer player, string command, string[] args) {
            CheckLocalBans();
        }

        [Command("serverarmour.cp")]
        void SCmdCheckPlayer(IPlayer player, string command, string[] args) {
            string playerArg = args[0];
            bool forceUpdate = false;

            bool.TryParse(args[1], out forceUpdate);

            IPlayer playerToCheck = players.FindPlayer(args[0].Trim());
            if (PlayerCached(playerToCheck.Id)) {

            }
            CheckLocalBans();
        }
        #endregion 

        #region Data Handling
        bool PlayerCached(string steamid) => _playerData.ContainsKey(steamid);
        bool PlayerDeleteCache(string steamid) => _playerData.Remove(steamid);
        void PlayerAddCached(string steamid, ISAPlayer player) => _playerData.Add(steamid, player);
        void PlayerAddCached(ISAPlayer isaplayer) => _playerData.Add(isaplayer.steamid, isaplayer);
        void PlayerAddCached(IPlayer iplayer, ISAPlayer isaplayer) => _playerData.Add(iplayer.Id, isaplayer);
        ISAPlayer PlayerGetCache(string steamid) => _playerData[steamid];
        ISAPlayer PlayerGetCache(IPlayer player) => _playerData[player.Id];
        List<ISABan> PlayerGetBanDataCache(string steamid) => _playerData[steamid].banData;
        int PlayerGetBanDataCountCache(string steamid) => _playerData[steamid].banData.Count;
        void PlayerSetBanDataCache(ISAPlayer isaplayer) => _playerData[isaplayer.steamid].banData = isaplayer.banData;
        void PlayerAddBanDataCache(IPlayer iplayer, ISABan isaban) => _playerData[iplayer.Id].banData.Add(isaban);


        T GetConfig<T>(string name, T defaultValue) => Config[name] == null ? defaultValue : (T)Convert.ChangeType(Config[name], typeof(T));
        protected override void LoadDefaultConfig() {
            LogWarning("Creating a new configuration file");
            Config.WriteObject(
                new ISAConfig {
                    AutoBanGroup = "serverarmour.bans",
                    AutoBanOn = true,
                    AutoBanCeiling = 1,
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

        protected override void LoadDefaultMessages() {
            lang.RegisterMessages(new Dictionary<string, string> {
                ["EpicThing"] = "An epic thing has happened"
            }, this, "en");
        }

        string GetMsg(IPlayer player, string key) {
            return lang.GetMessage(key, this, player.Id);
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

        void CheckLocalBans() {
            // player.BanTimeRemaining;
            int i = 1;
            foreach (ServerUsers.User usr in ServerUsers.GetAll(ServerUsers.UserGroup.Banned)) {

                bool containsMyBan = false;

                LogDebug($" check steamid '{usr.steamid.ToString()}'");
                if (PlayerCached(usr.steamid.ToString())) {
                    LogDebug("Player has " + PlayerGetBanDataCountCache(usr.steamid.ToString()) + "bans");
                    foreach (ISABan ban in PlayerGetBanDataCache(usr.steamid.ToString())) {
                        LogDebug($"Ban {i}: " + ban.serverIp);
                        i++;
                        Puts("serverIp " + ban.serverIp + " myserverip " + thisServerIp);
                        if (ban.serverIp.Equals(thisServerIp)) {
                            containsMyBan = true;
                            LogDebug("Contains my ban!");
                            break;
                        }
                    }
                }

                LogDebug("Ban flag is " + containsMyBan);

                if (!containsMyBan) {
                    IPlayer player = covalence.Players.FindPlayer(usr.steamid.ToString());
                    LogDebug("Submitting ban for player " + player.Name);
                    AddBan(player, usr.notes);
                }
            }
        }

        #endregion

        #region API Hooks
        private int API_GetBanCount(string steamid) => PlayerCached(steamid) ? PlayerGetBanDataCountCache(steamid) : 0;
        private bool API_GetIsPlayerDirty(string steamid) => PlayerCached(steamid) && PlayerGetBanDataCountCache(steamid) > 0;
        #endregion

        #region Classes 
        public class ISAPlayer {
            public string steamid;
            public string username;
            public int banCount;
            public List<ISABan> banData;
            public uint cacheTimestamp;
            public uint lastConnected;
        }

        public class ISABan {
            public string banId;
            public string serverName;
            public string serverIp;
            public string reason;
            public string date;
        }

        private class ISAConfig {
            public bool Debug;

            public string AutoBanGroup;
            public bool AutoBanOn;
            public int AutoBanCeiling;
            public string[] AutoBanReasonKeywords;

            public string WatchlistGroup;
            public int WatchlistCeiling;

            public string ServerName;
            public int ServerPort;
            public string ServerVersion;
            public string ServerAdminName;
            public string ServerAdminEmail;
            public string ServerApiKey;
        }
        #endregion
    }
}
