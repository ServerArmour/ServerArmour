using Newtonsoft.Json;
#if RUST
using Newtonsoft.Json.Linq;
#endif
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Time = Oxide.Core.Libraries.Time;


namespace Oxide.Plugins {
    [Info("Server Armour", "Pho3niX90", "0.1.3")]
    [Description("Protect your server! Auto ban known hacker, scripter and griefer accounts, and notify server owners of threats.")]
    class ServerArmour : CovalencePlugin {

        #region Variables
        Dictionary<string, ISAPlayer> _playerData = new Dictionary<string, ISAPlayer>();
        private Time _time = GetLibrary<Time>();
        private double cacheLifetime = 300; // minutes
        string[] groups;
        private ISAConfig config;
        string thisServerIp;
        int ConfigVersion = 4;
        string specifier = "G";
        int secondsBetweenWebRequests;
        CultureInfo culture = CultureInfo.CreateSpecificCulture("en-US");
        StringComparison defaultCompare = StringComparison.InvariantCultureIgnoreCase;
        #region Permissions
        const string PermissionToBan = "serverarmour.ban";
        const string DATE_FORMAT = "yyyy/MM/dd HH:mm";
        #endregion
        #endregion

        #region Plugins
        [PluginReference] Plugin BetterChat;
        [PluginReference] Plugin DiscordMessages;
        [PluginReference] Plugin Arkan;

        void DiscordSend(ISAPlayer iPlayer, string report, int type = 1) {
            DiscordSend(players.FindPlayer(iPlayer.steamid), type, report);
        }

        void DiscordSend(IPlayer iPlayer, int type = 1, string report = null) {
            if (config.DiscordWebhookURL.Length == 0 && !config.DiscordWebhookURL.Equals("https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks")) { Puts("Discord webhook not setup."); return; }
            if (type == 1) {
                List<EmbedFieldList> fields = new List<EmbedFieldList>();

                string playerReport = $"[{iPlayer.Name}\n{iPlayer.Id}](https://steamcommunity.com/profiles/{iPlayer.Id})";
                fields.Add(new EmbedFieldList() {
                    name = "Player ",
                    value = playerReport,
                    inline = true
                });
                if (report != null)
                    fields.Add(new EmbedFieldList() {
                        name = "Report ",
                        value = report,
                        inline = true
                    });
                var fieldsObject = fields.Cast<object>().ToArray();
                string json = JsonConvert.SerializeObject(fieldsObject);
                DiscordMessages?.Call("API_SendFancyMessage", config.DiscordWebhookURL, "Server Armour Report: ", 39423, json);
            }
        }

        #endregion

        #region Hooks
        void Init() {
            config = Config.ReadObject<ISAConfig>();

            LoadData();
            Puts("Server Armour is being initialized.");
            SaveConfig();

            CheckGroups();
            permission.RegisterPermission(PermissionToBan, this);
        }

        void OnServerInitialized() {

            thisServerIp = server.Address.ToString();
            Puts("Server IP " + thisServerIp + " local " + server.LocalAddress);
            config.ServerName = server.Name;
            config.ServerPort = server.Port;
            config.ServerVersion = server.Version;

            CheckOnlineUsers();
            CheckLocalBans();

            Puts("Server Armour finished initializing.");
            RegisterTag();
        }

        void Unload() {
            Puts("Server Armour unloading, will now save all data.");
            SaveData();
            Puts("Server Armour finished unloaded.");
        }

        void OnUserConnected(IPlayer player) {
            if (player == null) {
                Puts("The player that just logged in has returned a null object. This might be an error.");
                return;
            }
            ISABan lenderBan = null;
            ISABan ban = null;

            string lenderId = GetPlayerCache(player.Id)?.lendersteamid;
            if (lenderId != null && !lenderId.Equals("0")) {
                GetPlayerBans(lenderId, true);
                lenderBan = IsBanned(lenderId);
            }

            GetPlayerBans(player, true, "C");
            ban = IsBanned(player.Id);

            if (ban != null || lenderBan != null) player.Kick(ban.reason);

            if (config.ShowProtectedMsg) player.Reply(GetMsg("Protected MSG"));
        }

        void OnUserDisconnected(IPlayer player) {
            GetPlayerBans(player, true, "D");
        }

        bool HasRecentVac(string playerid) {
            if (!IsPlayerCached(playerid)) return false;
            ISASteamData cache = GetPlayerCache(playerid).steamData;
            return (cache.VACBanned > 0 || cache.NumberOfVACBans > 0) ? cache.DaysSinceLastBan < config.DissallowVacBanDays : false;
        }

        object CanUserLogin(string name, string id, string ip) {
            bool canLogin = !AssignGroupsAndBan(players.FindPlayer(name));
            ISABan ban = IsBanned(id);
            canLogin = ban != null ? false : canLogin;
            if (HasRecentVac(id)) {
                return false;
            }
            if (!canLogin) {
                Puts($"{ip}:{id}:{name} tried to connect. But the connection was rejected due to him being banned, Reason: " + ban.reason);
                return ban.reason;
            }
            return canLogin;
        }

        void OnPluginLoaded(Plugin plugin) {
            if (plugin.Title == "BetterChat") RegisterTag();
        }

        void OnUserUnbanned(string name, string id, string ipAddress) {
            Puts($"Player {name} ({id}) at {ipAddress} was unbanned");
            IPlayer iPlayer = players.FindPlayer(id);
            if (iPlayer != null)
                Unban(iPlayer);
        }
        #endregion

        #region WebRequests

        bool reportSent = false;
        void GetPlayerBans(IPlayer player, string type = "C") {
            GetPlayerBans(player, false, type);
        }

        void GetPlayerBans(IPlayer player, bool reCache = false, string type = "C") {
            bool isCached = player != null ? _playerData.ContainsKey(player.Id) : false;

            if (isCached) {
                if (!IsCacheValid(player.Id) || reCache) {
                    DeletePlayerCache(player.Id);
                    LogDebug($"Will now update local cache for player {player.Name}");
                    isCached = false;
                } else {
                    reportSent = true;
                    GetPlayerReport(player, player.IsConnected);
                    return; //user already cached, therefore do not check again before cache time laps.
                }
            } else {
                LogDebug($"Player {player.Name} not cached");
            }
            _webCheckPlayer(player.Name, player.Id, player.Address, player.IsConnected, type);
        }

        void GetPlayerBans(string playerId, bool reCache = false, string type = "C") {
            bool isCached = IsPlayerCached(playerId);
            uint currentTimestamp = _time.GetUnixTimestamp();

            if (isCached) {
                IPlayer player = players.FindPlayerById(playerId);
                double minutesOld = Math.Round((currentTimestamp - GetPlayerCache(playerId).cacheTimestamp) / 60.0);
                bool oldCache = minutesOld >= cacheLifetime;
                LogDebug($"Player {player.Name}'s cache is {minutesOld} minutes old. " + ((oldCache) ? "Cache is old" : "Cache is fresh"));
                if (oldCache || reCache) {
                    DeletePlayerCache(player.Id);
                    LogDebug($"Will now update local cache for player {player.Name}");
                    isCached = false;
                } else {
                    reportSent = true;
                    GetPlayerReport(player, player.IsConnected);
                    return; //user already cached, therefore do not check again before cache time laps.
                }
            }

            _webCheckPlayer("LENDER:UKNOWN", playerId, "", false, type);
        }

        void _webCheckPlayer(string name, string id, string address, Boolean connected, string type) {
            string playerName = Uri.EscapeDataString(name);
            string url = $"https://io.serverarmour.com/checkUser?steamid={id}&username={playerName}&ip={address}&t={type}" + ServerGetString();
            Puts(url);
            webrequest.Enqueue(url, null, (code, response) => {
                if (code != 200 || response == null) {
                    Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                    return;
                }
                ISAPlayer isaPlayer = JsonConvert.DeserializeObject<ISAPlayer>(response);
                isaPlayer.cacheTimestamp = _time.GetUnixTimestamp();
                isaPlayer.lastConnected = _time.GetUnixTimestamp();

                if (config.AutoKick_BadIp && isaPlayer.ipRating > 0.98 && connected) {
                    players.FindPlayerById(id)?.Kick(GetMsg("Reason: Bad IP"));
                }

                if (HasRecentVac(id)) {
                    //"VAC ban received {daysago}, wait another {daysto}"
                    int vacLast = GetPlayerCache(id).steamData.DaysSinceLastBan;
                    int until = config.DissallowVacBanDays - vacLast;
                    string msg = GetMsg("Reason: VAC Ban Too Fresh", new Dictionary<string, string> { ["daysago"] = vacLast.ToString(), ["daysto"] = until.ToString() });
                    players.FindPlayerById(id)?.Kick(msg);
                    Puts(id + " - " + msg);
                }

                if (!IsPlayerCached(isaPlayer.steamid)) {
                    AddPlayerCached(isaPlayer);
                } else {
                    SetPlayerBanData(isaPlayer);
                }

                if (!reportSent) {
                    GetPlayerReport(isaPlayer, connected);
                    reportSent = true;
                }


            }, this, RequestMethod.GET);
        }

        void _webAddArkan(string type, string userid, string violationProbability, string shotsCnt, string ammoShortName, string weaponShortName, string attachments, string suspiciousNoRecoilShots) {
            string url = "https://io.serverarmour.com/addArkan";
            Dictionary<string, string> parameters = new Dictionary<string, string>();
            webrequest.Enqueue(url + ServerGetString("?"), $"uid={userid}&type={type}&vp={violationProbability}&sc={shotsCnt}&ammo={ammoShortName}&weapon={weaponShortName}&attach={attachments}&snrs={suspiciousNoRecoilShots}", (code, response) => {
                if (code != 200 || response == null) {
                    Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                    return;
                }
            }, this, RequestMethod.POST);
        }

        void AddBan(IPlayer player, ISABan thisBan) {
            DateTime now = DateTime.Now;

            string url = $"https://io.serverarmour.com/addBan?steamid={player.Id}&username={player.Name}&ip={player.Address}&reason={thisBan.reason}&dateTime={thisBan.date}" + ServerGetString();
            webrequest.Enqueue(url, null, (code, response) => {
                if (code != 200 || response == null) { Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response })); return; }
                // ISABan thisBan = new ISABan { serverName = server.Name, date = dateTime, reason = banreason, serverIp = thisServerIp, banUntil = dateBanUntil };
                if (IsPlayerCached(player.Id)) {
                    LogDebug($"{player.Name} has ban cached, now updating.");
                    AddPlayerBanData(player, thisBan);
                } else {
                    LogDebug($"{player.Name} had no ban data cached, now creating.");
                    AddPlayerCached(player,
                        new ISAPlayer {
                            steamid = player.Id,
                            username = player.Name,
                            serverBanCount = 1,
                            cacheTimestamp = _time.GetUnixTimestamp(),
                            lastConnected = _time.GetUnixTimestamp(),
                            serverBanData = new List<ISABan> { thisBan }
                        });
                }
                //SaveData();
            }, this, RequestMethod.GET);
        }


        bool ContainsMyBan(string steamid) {
            return IsBanned(steamid) != null;
        }

        ISABan IsBanned(string steamid) {
            if (!IsPlayerCached(steamid)) return null;
            try {
                return GetPlayerCache(steamid)?.serverBanData.First(x => x.serverIp.Equals(thisServerIp));
            } catch (InvalidOperationException ioe) {
                return null;
            }
        }

        string GetBanReason(ISAPlayer isaPlayer) {
            return isaPlayer?.serverBanData.First(x => x.serverIp.Equals(thisServerIp)).reason;
        }
        #endregion

        #region Commands
        [Command("sa.clb", "getreport")]
        void SCmdCheckLocalBans(IPlayer player, string command, string[] args) {
            CheckLocalBans();
        }


        [Command("unban", "playerunban", "sa.unban")]
        void SCmdUnban(IPlayer player, string command, string[] args) {
            LogDebug("Will now unban");
            if (!HasPermission(player, PermissionToBan)) {
                player.Reply(GetMsg("NoPermission"));
                return;
            }

            if (args == null || (args.Length != 1)) {
                player.Reply(GetMsg("UnBan Syntax"));
                return;
            }

            IPlayer iPlayer = players.FindPlayer(args[0]);
            if (iPlayer == null) { GetMsg("Player Not Found", new Dictionary<string, string> { ["player"] = args[0] }); return; }

            if (!IsPlayerCached(iPlayer.Id) || !ContainsMyBan(iPlayer.Id)) {
                player.Reply(GetMsg("Player Not Banned"));
                return;
            }
            Unban(iPlayer);
        }

        void Unban(IPlayer iPlayer) {
            ISAPlayer isaPlayer = GetPlayerCache(iPlayer.Id);
            if (iPlayer.IsBanned) iPlayer.Unban();
            RemoveBans(isaPlayer);
        }

        int RemoveBans(ISAPlayer player) {
            return player.serverBanData.RemoveAll(x => x.serverIp == thisServerIp);
        }

        [Command("ban", "playerban", "sa.ban")]
        void SCmdBan(IPlayer player, string command, string[] args) {

            if (!HasPermission(player, PermissionToBan)) {
                player.Reply(GetMsg("NoPermission"));
                return;
            }
            int argsLength = args.Length;
            if (args == null || (argsLength < 2 || argsLength > 3)) {
                player.Reply(GetMsg("Ban Syntax"));
                return;
            }
            string banPlayer = args[0];

            string errMsg = "";
            IPlayer iPlayer = null;
            IEnumerable<IPlayer> playersFound = players.FindPlayers(banPlayer);
            int playersFoundCount = playersFound.Count();
            switch (playersFoundCount) {
                case 0:
                    errMsg = GetMsg("Player Not Found", new Dictionary<string, string> { ["player"] = banPlayer });
                    break;
                case 1:
                    iPlayer = players.FindPlayer(banPlayer);
                    break;
                default:
                    List<string> playersFoundNames = new List<string>();
                    for (int i = 0; i < playersFoundCount; i++) playersFoundNames.Add(playersFound.ElementAt(i).Name);
                    string playersFoundNamesString = String.Join(", ", playersFoundNames.ToArray());
                    errMsg = GetMsg("Multiple Players Found", new Dictionary<string, string> { ["players"] = playersFoundNamesString });
                    break;
            }

            if (iPlayer == null || !errMsg.Equals("")) { player.Reply(errMsg); return; }

            string banReason = args[1];
            int banDuration = 0;

            if (argsLength == 3) {
                try {
                    int.TryParse(args[2], out banDuration);
                } catch (Exception e) {
                    player.Reply(GetMsg("Ban Syntax"));
                    return;
                }
            }

            ISAPlayer isaPlayer;

            if (!IsPlayerCached(iPlayer.Id)) {
                isaPlayer = new ISAPlayer().CreatePlayer(iPlayer);
                AddPlayerCached(isaPlayer);
            } else {
                isaPlayer = GetPlayerCache(banPlayer);
            }

            DateTime now = DateTime.Now;
            string dateTime = now.ToString(DATE_FORMAT);
            uint dateBanUntil = ConvertToTimestamp(now.AddDays(banDuration == 0 ? 3650 : banDuration));

            if (BanPlayer(iPlayer,
                new ISABan {
                    serverName = server.Name,
                    serverIp = thisServerIp,
                    reason = banReason,
                    date = dateTime,
                    banUntil = dateBanUntil
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
            string playerArg = (args.Length == 0) ? player.Id : args[0];
            bool forceUpdate = false;

            if (args.Length > 1) {
                bool.TryParse(args[1], out forceUpdate);
            }

            IPlayer playerToCheck = players.FindPlayer(playerArg.Trim());
            if (playerToCheck == null) {
                player.Reply(GetMsg("Player Not Found", new Dictionary<string, string> { ["player"] = playerArg }));
                return;
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
            AddBan(iPlayer, ban);
            NativeBan(iPlayer, ban);

            if (iPlayer.IsConnected)
                iPlayer.Kick(ban.reason);
            return true;
        }

        void NativeBan(IPlayer iPlayer, ISABan ban) {
            uint secondsLeft = ban.banUntil - _time.GetUnixTimestamp();

            if (!iPlayer.IsBanned) iPlayer.Ban(ban.reason, TimeSpan.FromSeconds(secondsLeft));
        }
        #endregion

        #region IEnumerators

        void CheckOnlineUsers() {
            IEnumerable<IPlayer> allPlayers = players.Connected;
            int allPlayersCount = allPlayers.Count();
            int allPlayersCounter = 0;
            float waitTime = 0.2f;
            if (allPlayersCount > 0)
                timer.Repeat(waitTime, allPlayersCount, () => {
                    LogDebug("Will now inspect all online users, time etimation: " + (allPlayersCount * waitTime) + " seconds");
                    LogDebug($"Inpecting online user {allPlayersCounter + 1} of {allPlayersCount} for infractions");
                    IPlayer player = allPlayers.ElementAt(allPlayersCounter);
                    if (player != null) GetPlayerBans(player, true);
                    if (allPlayersCounter < allPlayersCount) LogDebug("Inspection completed.");
                    allPlayersCounter++;
                });
        }

        void CheckLocalBans() {
#if RUST
            IEnumerable<ServerUsers.User> bannedUsers = ServerUsers.GetAll(ServerUsers.UserGroup.Banned);
            int BannedUsersCount = bannedUsers.Count();
            int BannedUsersCounter = 0;
            float waitTime = 1f;

            if (BannedUsersCount > 0)
                timer.Repeat(waitTime, BannedUsersCount, () => {
                    ServerUsers.User usr = bannedUsers.ElementAt(BannedUsersCounter);
                    LogDebug($"Checking local user ban {BannedUsersCounter + 1} of {BannedUsersCounter}");
                    if (IsBanned(usr.steamid.ToString(specifier, culture)) == null) {
                        IPlayer player = covalence.Players.FindPlayer(usr.steamid.ToString(specifier, culture));
                        AddBan(player, new ISABan {
                            serverName = server.Name,
                            serverIp = thisServerIp,
                            reason = usr.notes,
                            date = DateTime.Now.ToString(DATE_FORMAT),
                            banUntil = (uint)usr.expiry
                        });
                    }

                    BannedUsersCounter++;
                });
#endif
        }
        #endregion

        #region Data Handling

        bool IsPlayerDirty(ISAPlayer isaPlayer) {
            return (isaPlayer.serverBanCount > 0 || isaPlayer.steamData.CommunityBanned > 0 || isaPlayer.steamData.NumberOfGameBans > 0 || isaPlayer.steamData.VACBanned > 0);
        }

        bool IsPlayerDirty(string steamid) {
            ISAPlayer isaPlayer = GetPlayerCache(steamid);
            return IsPlayerCached(steamid) && (isaPlayer.serverBanCount > 0 || isaPlayer.steamData.CommunityBanned > 0 || isaPlayer.steamData.NumberOfGameBans > 0 || isaPlayer.steamData.VACBanned > 0);
        }

        bool IsLenderDirty(string steamid) {
            ISAPlayer isaPlayer = GetPlayerCache(steamid);
            if (isaPlayer.lendersteamid.Equals("0")) return false;
            ISAPlayer isaLender = GetPlayerCache(isaPlayer.lendersteamid);
            return IsPlayerDirty(isaLender);
        }

        bool IsFamilyShare(string steamid) {
            ISAPlayer player = GetPlayerCache(steamid);
            return !GetFamilyShareLenderSteamId(steamid).Equals("0");
        }

        string GetFamilyShareLenderSteamId(string steamid) {
            ISAPlayer player = GetPlayerCache(steamid);
            return (player != null && !player.lendersteamid.Equals("0") && !player.lendersteamid.Equals("")) ? player.lendersteamid : "0";
        }

        bool IsPlayerCached(string steamid) { return _playerData != null && _playerData.Count > 0 && _playerData.ContainsKey(steamid); }
        bool DeletePlayerCache(string steamid) { bool res = _playerData.Remove(steamid); SaveData(); return res; }
        void AddPlayerCached(ISAPlayer isaplayer) => _playerData.Add(isaplayer.steamid, isaplayer);
        void AddPlayerCached(IPlayer iplayer, ISAPlayer isaplayer) => _playerData.Add(iplayer.Id, isaplayer);
        ISAPlayer GetPlayerCache(string steamid) => IsPlayerCached(steamid) ? _playerData[steamid] : null;
        List<ISABan> GetPlayerBanData(string steamid) => _playerData[steamid].serverBanData;
        int GetPlayerBanDataCount(string steamid) => _playerData[steamid].serverBanData.Count;
        void SetPlayerBanData(ISAPlayer isaplayer) => _playerData[isaplayer.steamid].serverBanData = isaplayer.serverBanData;
        void AddPlayerBanData(IPlayer iplayer, ISABan isaban) => _playerData[iplayer.Id].serverBanData.Add(isaban);
        IPlayer FindIPlayer(string identifier) => players.FindPlayer(identifier);

        bool IsCacheValid(string id) {
            if (!_playerData.ContainsKey(id)) return false;

            uint currentTimestamp = _time.GetUnixTimestamp();
            double minutesOld = Math.Round((currentTimestamp - _playerData[id].cacheTimestamp) / 60.0);

            return minutesOld < cacheLifetime;
        }

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

            Dictionary<string, string> data =
                       new Dictionary<string, string> {
                           ["status"] = IsPlayerDirty(isaPlayer.steamid) ? "dirty" : "clean",
                           ["steamid"] = isaPlayer.steamid,
                           ["username"] = isaPlayer.username,
                           ["serverBanCount"] = isaPlayer.serverBanCount.ToString(),
                           ["NumberOfGameBans"] = isaPlayer.steamData.NumberOfGameBans.ToString(),
                           ["NumberOfVACBans"] = isaPlayer.steamData.NumberOfVACBans.ToString() + (isaPlayer.steamData.NumberOfVACBans > 0 ? $" Last({isaPlayer.steamData.DaysSinceLastBan}) days ago" : ""),
                           ["EconomyBan"] = (!isaPlayer.steamData.EconomyBan.Equals("none")).ToString(),
                           ["FamShare"] = IsFamilyShare(isaPlayer.steamid).ToString() + (IsFamilyShare(isaPlayer.steamid) ? IsLenderDirty(isaPlayer.steamid) ? "DIRTY" : "CLEAN" : "")
                       };

            if (IsPlayerDirty(isaPlayer.steamid) || isCommand) {
                string report = GetMsg("User Dirty MSG", data);
                if (config.BroadcastPlayerBanReport && isConnected && !isCommand && config.BroadcastPlayerBanReportVacDays > isaPlayer.steamData.DaysSinceLastBan) {
                    server.Broadcast(report.Replace(isaPlayer.steamid + ":", string.Empty).Replace(isaPlayer.steamid, string.Empty));
                }
                if (isCommand) {
                    cmdPlayer.Reply(report.Replace(isaPlayer.steamid + ":", string.Empty).Replace(isaPlayer.steamid, string.Empty));
                }
                //Puts(report);
            }

            if ((config.DiscordOnlySendDirtyReports && IsPlayerDirty(isaPlayer.steamid)) || !config.DiscordOnlySendDirtyReports)
                DiscordSend(isaPlayer, GetMsg("User Dirty DISCORD MSG", data));
        }


        private void SaveConfig() {
            Config.WriteObject(config, true);
        }
        protected override void LoadDefaultConfig() {
            Config.WriteObject(GetDefaultConfig(), true);
        }
        private ISAConfig GetDefaultConfig() {
            return new ISAConfig {

                Version = ConfigVersion,
                Debug = false,

                ShowProtectedMsg = true,
                AutoBanGroup = "serverarmour.bans",
                AutoBanOn = true,
                AutoBanCeiling = 5,
                AutoVacBanCeiling = 2,
                DissallowVacBanDays = 90, // 
                AutoBanFamilyShare = false,
                AutoBanFamilyShareIfDirty = false,
                WatchlistGroup = "serverarmour.watchlist",
                WatchlistCeiling = 1,
                BetterChatDirtyPlayerTag = "",
                BroadcastPlayerBanReport = true,
                BroadcastPlayerBanReportVacDays = 120, //
                BroadcastNewBans = true,
                ServerName = server.Name,
                ServerPort = server.Port,
                ServerVersion = server.Version,
                ServerAdminShareDetails = true,
                ServerAdminName = string.Empty,
                ServerAdminEmail = string.Empty,
                ServerApiKey = "FREE",

                AutoBan_Reason_Keyword_Aimbot = false,
                AutoBan_Reason_Keyword_Hack = false,
                AutoBan_Reason_Keyword_EspHack = false,
                AutoBan_Reason_Keyword_Script = false,
                AutoBan_Reason_Keyword_Cheat = false,
                AutoBan_Reason_Keyword_Toxic = false,
                AutoBan_Reason_Keyword_Insult = false,
                AutoBan_Reason_Keyword_Ping = false,
                AutoBan_Reason_Keyword_Racism = false,
                AutoKick_BadIp = false,

                DiscordWebhookURL = "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks",
                DiscordOnlySendDirtyReports = true,
                SubmitArkanData = true
            };
        }

        private void LogDebug(string txt) {
            if (config.Debug) Puts(txt);
        }

        void SaveData() {
            if (_playerData.Count > 0) {
                try {
                    Interface.Oxide.DataFileSystem.WriteObject<Dictionary<string, ISAPlayer>>("ServerArmour", _playerData, true);
                } catch (Exception exc) {
                    Puts("An error occured writing data file.");
                    Puts(exc.ToString());
                }
            }
        }

        void LoadData() {
            if (Interface.Oxide.DataFileSystem.ExistsDatafile("ServerArmour")) {
                try {
                    _playerData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, ISAPlayer>>("ServerArmour");
                } catch (Exception exc) {
                    Puts("An error occured loading data file.");
                    Puts(exc.ToString());
                }
                Puts("DATA loaded");
            } else {
                SaveData();
            }
        }

        void CheckGroups() {
            LogDebug("Registering groups");

            string[] groups = permission.GetGroups();

            LogDebug("Checking if config groups exists.");

            string autobanGroup = config.AutoBanGroup;
            string watchlistGroup = config.WatchlistGroup;

            if (!permission.GroupExists(autobanGroup)) {
                permission.CreateGroup(autobanGroup, "Server Armour Autobans", 0);
            }

            if (!permission.GroupExists(watchlistGroup)) {
                permission.CreateGroup(watchlistGroup, "Server Armour Watchlist", 0);
            }
        }

        string ServerGetString() {
            return ServerGetString("&");
        }

        string ServerGetString(string start) {
            return start + $"sip={thisServerIp}&sn={config.ServerName}&sp={config.ServerPort}&an={config.ServerAdminName}&ae={config.ServerAdminEmail}&gameId={covalence.ClientAppId}&gameName={covalence.Game}";
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
                    Dictionary<string, string> data =
                       new Dictionary<string, string> {
                           ["username"] = isaPlayer.username,
                           ["reason"] = GetBanReason(isaPlayer)
                       };
                    server.Broadcast(GetMsg("Broadcast Player Banned", data));
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
                    if (keywordBan) break;
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
        private int API_GetDaysSinceLastVacBan(string steamid) => IsPlayerCached(steamid) ? GetPlayerCache(steamid).steamData.DaysSinceLastBan : 0;
        private int API_GetGameBanCount(string steamid) => IsPlayerCached(steamid) ? GetPlayerCache(steamid).steamData.NumberOfGameBans : 0;
        private string API_GetEconomyBanStatus(string steamid) => IsPlayerCached(steamid) ? GetPlayerCache(steamid).steamData.EconomyBan : "none";
        private bool API_GetIsPlayerDirty(string steamid) => IsPlayerDirty(steamid);
        private bool API_GetIsFamilyShareLenderDirty(string steamid) => IsLenderDirty(steamid);
        private bool API_GetIsFamilyShare(string steamid) => IsFamilyShare(steamid);
        private string API_GetFamilyShareLenderSteamId(string steamid) => GetFamilyShareLenderSteamId(steamid);
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
                ["Protected MSG"] = "Server protected by [#008080ff]ServerArmour[/#]",
                ["User Dirty MSG"] = "[#008080ff]Server Armour Report:\n {steamid}:{username}[/#] is {status}.\n [#ff0000ff]Server Bans:[/#] {serverBanCount}\n [#ff0000ff]Game Bans:[/#] {NumberOfGameBans}\n [#ff0000ff]Vac Bans:[/#] {NumberOfVACBans}\n [#ff0000ff]Economy Banned:[/#] {EconomyBan}\n [#ff0000ff]Family Share:[/#] {FamShare}",
                ["User Dirty DISCORD MSG"] = "**Server Bans:** {serverBanCount}\n **Game Bans:** {NumberOfGameBans}\n **Vac Bans:** {NumberOfVACBans}\n **Economy Banned:** {EconomyBan}\n **Family Share:** {FamShare}",
                ["Command sa.cp Error"] = "Wrong format, example: /sa.cp usernameORsteamid trueORfalse",
                ["Arkan No Recoil Violation"] = "[#ff0000]{player}[/#] received an Arkan no recoil violation.\n[#ff0000]Violation[/#] #{violationNr}, [#ff0000]Weapon:[/#] {weapon}, [#ff0000]Ammo:[/#] {ammo}, [#ff0000]Shots count:[/#] {shots}\n Admins will investigate ASAP, please have handcams ready.\n This might be a false-positive, but all violations need to be investigated.",
                ["Arkan Aimbot Violation"] = "[#ff0000]{player}[/#] received an Arkan aimbot violation.\n[#ff0000]Violation[/#]  #{violationNr}, [#ff0000]Weapon:[/#] {weapon}, [#ff0000]Ammo:[/#] {ammo}\n Admins will investigate ASAP, please have handcams ready.\n This might be a false-positive, but all violations need to be investigated.",
                ["Arkan In Rock Violation"] = "[#ff0000]{player}[/#] received an Arkan in rock violation.\n[#ff0000]Violation[/#]  #{violationNr}, [#ff0000]Weapon:[/#] {weapon}, [#ff0000]Ammo:[/#] {ammo}\n Admins will investigate ASAP, please have handcams ready.\n This might be a false-positive, but all violations need to be investigated.",
                ["Player Now Banned"] = "[#ff0000]{player}[/#] has been banned\n[#ff0000]Reason: [/#] {reason}",
                ["Reason: Bad IP"] = "Bad IP Detected, either due to a VPN/Proxy",
                ["Player Not Found"] = "Player wasn't found",
                ["Multiple Players Found"] = "Multiple players found with that name ({players}), please try something more unique like a steamid",
                ["Ban Syntax"] = "sa.ban <playerNameOrID> \"<reason>\" [duration days: default 3650]",
                ["UnBan Syntax"] = "sa.unban <playerNameOrID>",
                ["No Response From API"] = "Couldn't get an answer from ServerArmour.com! Error: {code} {response}",
                ["Player Not Banned"] = "Player not banned",
                ["Broadcast Player Banned"] = "{tag} {username} wasn't allowed to connect\nReason: {reason}",
                ["Reason: VAC Ban Too Fresh"] = "VAC ban received {daysago}, wait another {daysto}"
            }, this);
        }

        bool HasPermission(IPlayer player, string permissionName) {
            if (player.IsAdmin) return true;
            return permission.UserHasPermission(player.Id, permissionName);
        }
        #endregion

        #region Plugins methods
        string GetChatTag() => "[<#008080ff][Server Armour]: [/#]";
        void RegisterTag() {
            if (BetterChat != null && config.BetterChatDirtyPlayerTag != null && config.BetterChatDirtyPlayerTag.Length > 0)
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

        #region Helpers 
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static uint ConvertToTimestamp(DateTime value) {
            TimeSpan elapsedTime = value - Epoch;
            return (uint)elapsedTime.TotalSeconds;
        }
        #endregion

        #region Classes 

        public class EmbedFieldList {
            public string name { get; set; }
            public string value { get; set; }
            public bool inline { get; set; }
        }

        public class ISAPlayer {
            public string steamid { get; set; }
            public string lendersteamid { get; set; }
            public ISASteamData lenderSteamData { get; set; }
            public string username { get; set; }
            public ISASteamData steamData { get; set; }
            public int serverBanCount { get; set; }
            public List<ISABan> serverBanData { get; set; }
            public uint cacheTimestamp { get; set; }
            public uint lastConnected { get; set; }
            public double ipRating { get; set; }

            public ISAPlayer CreatePlayer(IPlayer bPlayer) {
                steamid = bPlayer.Id;
                username = bPlayer.Name;
                cacheTimestamp = new Time().GetUnixTimestamp();
                lendersteamid = "0";
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
            public uint banUntil;
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
            public int CommunityBanned { get; set; }
            public int VACBanned { get; set; }
            public int NumberOfVACBans { get; set; }
            public int DaysSinceLastBan { get; set; }
            public int NumberOfGameBans { get; set; }
            public string EconomyBan { get; set; }
            public string BansLastCheckUTC { get; set; }
        }

        public class ISAConfig {
            public int Version;
            public bool Debug; //should always be false, unless explicitly asked to turn on, will cause performance issue when on.
            public bool ShowProtectedMsg; // Show the protected by ServerArmour msg?
            public string AutoBanGroup; // the group name that banned users should be added in
            public bool AutoBanOn; // turn auto banning on or off. 
            public int AutoBanCeiling; // Auto ban players with X amount of previous bans.
            public int AutoVacBanCeiling; //  Auto ban players with X amount of vac bans.
            public int DissallowVacBanDays; // users who have been vac banned in this amount of days will not be allowed to connect. 

            public bool AutoBanFamilyShare;
            public bool AutoBanFamilyShareIfDirty;

            public string WatchlistGroup; // the group name that watched users should be added in
            public int WatchlistCeiling; // Auto add players with X amount of previous bans to a watchlist.

            public string BetterChatDirtyPlayerTag; // tag for players that are dirty.
            public bool BroadcastPlayerBanReport; // tag for players that are dirty.
            public int BroadcastPlayerBanReportVacDays; // if a user has a vac ban older than this, then ignore

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

            public string DiscordWebhookURL;
            public bool DiscordOnlySendDirtyReports;
            public bool SubmitArkanData;
        }
        #endregion

        #region Plugin Classes & Hooks Rust

        #region Arkan
#if RUST
        private void API_ArkanOnNoRecoilViolation(BasePlayer player, int NRViolationsNum, string jString) {
            if (jString != null) {
                JObject aObject = JObject.Parse(jString);

                string shotsCnt = aObject.GetValue("ShotsCnt").ToString();
                string violationProbability = aObject.GetValue("violationProbability").ToString();
                string ammoShortName = aObject.GetValue("ammoShortName").ToString();
                string weaponShortName = aObject.GetValue("weaponShortName").ToString();
                string attachments = String.Join(", ", aObject.GetValue("attachments").Select(jv => (string)jv).ToArray());
                string suspiciousNoRecoilShots = aObject.GetValue("suspiciousNoRecoilShots").ToString();

                _webAddArkan("NR", player.UserIDString, violationProbability, shotsCnt, ammoShortName, weaponShortName, attachments, suspiciousNoRecoilShots);
            }
        }
        
        private void API_ArkanOnAimbotViolation(BasePlayer player, int AIMViolationsNum, string json) {
            if (json != null) {
                // Puts("Arkan: " + json);
            }
        }

        private void API_ArkanOnInRockViolation(BasePlayer player, int IRViolationsNum, string json) {
            if (json != null) {
                // Puts("Arkan: " + json);
            }
        }
#endif

        #endregion
        #endregion
    }
}
