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
    [Info("Server Armour", "Pho3niX90", "0.2.0")]
    [Description("Protect your server! Auto ban known hacker, scripter and griefer accounts, and notify server owners of threats.")]
    class ServerArmour : CovalencePlugin {

        #region Variables
        Dictionary<string, ISAPlayer> _playerData = new Dictionary<string, ISAPlayer>();
        private Time _time = GetLibrary<Time>();
        private double cacheLifetime = 60; // minutes
        string[] groups;
        private SAConfig config;
        string specifier = "G";
        int secondsBetweenWebRequests;
        CultureInfo culture = CultureInfo.CreateSpecificCulture("en-US");
        StringComparison defaultCompare = StringComparison.InvariantCultureIgnoreCase;
        const string DATE_FORMAT = "yyyy/MM/dd HH:mm";
        #region Permissions
        const string PermissionToBan = "serverarmour.ban";
        const string PermissionToUnBan = "serverarmour.unban";
        #endregion
        #endregion

        #region Plugins
        [PluginReference] Plugin BetterChat;
        [PluginReference] Plugin DiscordMessages;
        [PluginReference] Plugin Arkan;
        [PluginReference] Plugin Ember; //TODO add support
        [PluginReference] Plugin EnhancedBanSystem; //TODO add support

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
            config = new SAConfig(this);

            LoadData();
            Puts("Server Armour is being initialized.");
            SaveConfig();

            CheckGroups();
            permission.RegisterPermission(PermissionToBan, this);
            permission.RegisterPermission(PermissionToUnBan, this);
        }

        void OnServerInitialized() {
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

            GetPlayerBans(player, true, "C");

            if (config.ShowProtectedMsg) player.Reply(GetMsg("Protected MSG"));
        }

        void OnUserDisconnected(IPlayer player) {
            GetPlayerBans(player, true, "D");
        }

        void OnPluginLoaded(Plugin plugin) {
            if (plugin.Title == "BetterChat") RegisterTag();
        }

        void OnUserUnbanned(string name, string id, string ipAddress) {
            Puts($"Player {name} ({id}) at {ipAddress} was unbanned");
            IPlayer iPlayer = players.FindPlayer(id);
            Unban(iPlayer);
        }
        #endregion

        #region WebRequests

        bool reportSent = false;

        void GetPlayerBans(IPlayer player, bool reCache = false, string type = "C") {
            KickIfBanned(GetPlayerCache(player.Id));
            _webCheckPlayer(player.Name, player.Id, player.Address, player.IsConnected, type);
        }

        void _webCheckPlayer(string name, string id, string address, Boolean connected, string type) {
            string playerName = Uri.EscapeDataString(name);
            string url = $"https://io.serverarmour.com/checkUser?steamid={id}&username={playerName}&ip={address}&t={type}" + ServerGetString();

            webrequest.Enqueue(url, null, (code, response) => {
                if (code != 200 || response == null) {
                    Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                    return;
                }

                ISAPlayer isaPlayer = JsonConvert.DeserializeObject<ISAPlayer>(response);
                isaPlayer.cacheTimestamp = _time.GetUnixTimestamp();
                isaPlayer.lastConnected = _time.GetUnixTimestamp();

                bool Whitelisted = permission.UserHasGroup(isaPlayer.steamid, config.WhitelistGroup);
                if (!Whitelisted) {

                    // lets check bans first
                    KickIfBanned(isaPlayer);

                    // now lets check for a recent vac
                    if (config.AutoKickOn && HasRecentVac(isaPlayer?.steamid)) {
                        int vacLast = GetPlayerCache(isaPlayer?.steamid).steamData.DaysSinceLastBan;
                        int until = config.DissallowVacBanDays - vacLast;
                        string msg = GetMsg("Reason: VAC Ban Too Fresh", new Dictionary<string, string> { ["daysago"] = vacLast.ToString(), ["daysto"] = until.ToString() });
                        KickPlayer(isaPlayer?.steamid, msg);
                    }

                    // lets check if this user is using VPN
                    if (config.AutoKickOn && config.AutoKick_BadIp && IsVpn(isaPlayer)) {
                        KickPlayer(isaPlayer?.steamid, GetMsg("Reason: Bad IP"));
                    }

                    // does the user contain a keyword ban
                    if (HasKeywordBan(isaPlayer)) {
                        KickPlayer(isaPlayer?.steamid, GetMsg("Keyword Kick"));
                    }
                }

                // add cache for player
                if (!IsPlayerCached(isaPlayer.steamid)) {
                    AddPlayerCached(isaPlayer);
                } else {
                    UpdatePlayerData(isaPlayer);
                }

                if (!reportSent) {
                    GetPlayerReport(isaPlayer, connected);
                    reportSent = true;
                }


            }, this, RequestMethod.GET);
        }

        void KickIfBanned(ISAPlayer isaPlayer) {
            if (isaPlayer == null) return;

            ISABan lenderBan = IsBanned(isaPlayer?.lendersteamid);
            ISABan ban = IsBanned(isaPlayer?.steamid);
            if (ban != null) KickPlayer(isaPlayer?.steamid, ban.reason);
            if (lenderBan != null) KickPlayer(isaPlayer?.steamid, GetMsg("Lender Banned"));
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

            string url = $"https://io.serverarmour.com/addBan?steamid={player.Id}&username={player.Name}&ip={player.Address}&reason={thisBan.reason}&dateTime={thisBan.date}&dateUntil={thisBan.banUntil}" + ServerGetString();
            webrequest.Enqueue(url, null, (code, response) => {
                if (code != 200 || response == null) { Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response })); return; }
                // ISABan thisBan = new ISABan { serverName = server.Name, date = dateTime, reason = banreason, serverIp = thisServerIp, banUntil = dateBanUntil };
                if (IsPlayerCached(player.Id)) {
                    LogDebug($"{player.Name} has ban cached, now updating.");
                    AddPlayerData(player, thisBan);
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

        string GetBanReason(ISAPlayer isaPlayer) {
            return isaPlayer?.serverBanData.First(x => x.serverIp.Equals(server.Address)).reason;
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
            if (!HasPermission(player, PermissionToUnBan)) {
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

        [Command("ban", "playerban", "sa.ban")]
        void SCmdBan(IPlayer player, string command, string[] args) {

            if (!HasPermission(player, PermissionToBan)) {
                player.Reply(GetMsg("NoPermission"));
                return;
            }
            int argsLength = args.Length;
            /***
             * Length 2: player, reason
             * Length 3: player, reason, time
             ***/
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

            /***
             * If time specified, default to 100 years
             ***/
            string lengthOfBan = argsLength != 3 ? "100y" : args[2];

            ISAPlayer isaPlayer;

            if (!IsPlayerCached(iPlayer.Id)) {
                isaPlayer = new ISAPlayer().CreatePlayer(iPlayer);
                AddPlayerCached(isaPlayer);
            } else {
                isaPlayer = GetPlayerCache(banPlayer);
            }

            DateTime now = DateTime.Now;
            string dateTime = now.ToString(DATE_FORMAT);
            string dateBanUntil = _BanUntil(args[2]).ToString(DATE_FORMAT);

            if (BanPlayer(iPlayer,
                new ISABan {
                    serverName = server.Name,
                    serverIp = server.Address.ToString(),
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

            IPlayer playerToCheck = players.FindPlayer(playerArg.Trim());
            if (playerToCheck == null) {
                player.Reply(GetMsg("Player Not Found", new Dictionary<string, string> { ["player"] = playerArg }));
                return;
            }
            
            GetPlayerReport(playerToCheck, player);
        }
        #endregion

        #region VPN/Proxy
        #endregion

        #region Ban System

        DateTime _BanUntil(string banLength) {
            int digit = int.Parse(new string(banLength.Where(char.IsDigit).ToArray()));
            string del = new string(banLength.Where(char.IsLetter).ToArray());


            DateTime now = DateTime.Now;
            string dateTime = now.ToString(DATE_FORMAT);
            DateTime dateBanUntil;

            switch (del.ToUpper()) {
                case "MI":
                    dateBanUntil = now.AddMinutes(digit);
                    break;
                case "H":
                    dateBanUntil = now.AddHours(digit);
                    break;
                case "D":
                    dateBanUntil = now.AddDays(digit);
                    break;
                case "M":
                    dateBanUntil = now.AddMinutes(digit);
                    break;
                case "Y":
                    dateBanUntil = now.AddYears(digit);
                    break;
                default:
                    dateBanUntil = now.AddDays(digit);
                    break;
            }
            return dateBanUntil;
        }

        void Unban(IPlayer iPlayer) {
            if (iPlayer.IsBanned) iPlayer.Unban();
            RemoveBans(iPlayer.Id);
        }

        void RemoveBans(string id) {
            if (_playerData.ContainsKey(id)) {
                _playerData[id].serverBanData.RemoveAll(x => x.serverIp == server.Address.ToString());
                SavePlayerData(id);
            }
        }

        bool BanPlayer(IPlayer iPlayer, ISABan ban) {
            AddBan(iPlayer, ban);
            KickPlayer(iPlayer.Id, ban.reason);
            return true;
        }
        #endregion

        #region IEnumerators

        void CheckOnlineUsers() {
            IEnumerable<IPlayer> allPlayers = players.Connected;
            int allPlayersCount = allPlayers.Count();
            int allPlayersCounter = 0;
            float waitTime = 1f;
            if (allPlayersCount > 0)
                timer.Repeat(waitTime, allPlayersCount, () => {
                    LogDebug("Will now inspect all online users, time etimation: " + (allPlayersCount * waitTime) + " seconds");
                    LogDebug($"Inpecting online user {allPlayersCounter + 1} of {allPlayersCount} for infractions");
                    IPlayer player = allPlayers.ElementAt(allPlayersCounter);
                    if (player != null) GetPlayerBans(player, true, "U");
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
        string GetFamilyShareLenderSteamId(string steamid) {
            return GetPlayerCache(steamid)?.lendersteamid;
        }

        bool IsPlayerDirty(ISAPlayer isaPlayer) {
            return (isaPlayer.serverBanCount > 0 || isaPlayer.steamData.CommunityBanned > 0 || isaPlayer.steamData.NumberOfGameBans > 0 || isaPlayer.steamData.VACBanned > 0);
        }

        bool IsPlayerDirty(string steamid) {
            ISAPlayer isaPlayer = GetPlayerCache(steamid);
            return IsPlayerCached(steamid) && (isaPlayer.serverBanCount > 0 || isaPlayer.steamData.CommunityBanned > 0 || isaPlayer.steamData.NumberOfGameBans > 0 || isaPlayer.steamData.VACBanned > 0);
        }

        bool IsPlayerCached(string steamid) { return _playerData != null && _playerData.Count > 0 && _playerData.ContainsKey(steamid); }
        void AddPlayerCached(ISAPlayer isaplayer) => _playerData.Add(isaplayer.steamid, isaplayer);
        void AddPlayerCached(IPlayer iplayer, ISAPlayer isaplayer) => _playerData.Add(iplayer.Id, isaplayer);
        ISAPlayer GetPlayerCache(string steamid) {
            LoadPlayerData(steamid);
            return IsPlayerCached(steamid) ? _playerData[steamid] : null;
        }
        List<ISABan> GetPlayerBanData(string steamid) => _playerData[steamid].serverBanData;
        int GetPlayerBanDataCount(string steamid) => _playerData[steamid].serverBanData.Count;
        void UpdatePlayerData(ISAPlayer isaplayer) => _playerData[isaplayer.steamid] = isaplayer;
        void AddPlayerData(IPlayer iplayer, ISABan isaban) => _playerData[iplayer.Id].serverBanData.Add(isaban);
        IPlayer FindIPlayer(string identifier) => players.FindPlayer(identifier);

        bool IsCacheValid(string id) {
            LoadPlayerData(id);
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

        private void LogDebug(string txt) {
            if (config.Debug) Puts(txt);
        }

        void LoadData() {
            IEnumerable<IPlayer> connectedPlayers = players.Connected;
            int playerCount = connectedPlayers.Count();
            int playerCounter = 0;
            /**
             * not as pretty as foreach, but faster
             */
            while (playerCounter < playerCount) {
                LoadPlayerData(connectedPlayers.ElementAt(playerCounter)?.Id);
                playerCounter++;
            }
        }

        void SaveData() {
            int dataCount = _playerData.Count();
            int dataCounter = 0;
            /**
             * not as pretty as foreach, but faster
             */
            while (dataCounter < dataCount) {
                SavePlayerData(_playerData.ElementAt(dataCounter).Value.steamid);
                dataCounter++;
            }
        }

        void LoadPlayerData(string id) {
            if (!_playerData.ContainsKey(id)) {
                ISAPlayer playerData = Interface.Oxide.DataFileSystem.ReadObject<ISAPlayer>($"ServerArmour/{id}");
                if (playerData != null) {
                    _playerData.Add(id, playerData);
                }
            }
        }

        void SaveThenPurge(string id) {
            SavePlayerData(id);
            _playerData.Remove(id);
        }

        void SavePlayerData(string id) {
            if (_playerData.ContainsKey(id)) {
                Interface.Oxide.DataFileSystem.WriteObject($"ServerArmour/{id}", _playerData[id], true);
            }
        }

        void CheckGroups() {
            LogDebug("Registering groups");

            string[] groups = permission.GetGroups();

            LogDebug("Checking if config groups exists.");

            if (!permission.GroupExists(config.AutoKickGroup)) {
                permission.CreateGroup(config.AutoKickGroup, "Server Armour AutoKicks", 0);
            }

            if (!permission.GroupExists(config.WatchlistGroup)) {
                permission.CreateGroup(config.WatchlistGroup, "Server Armour Watchlist", 0);
            }

            if (!permission.GroupExists(config.WhitelistGroup)) {
                permission.CreateGroup(config.WhitelistGroup, "Server Armour Whitelist", 0);
            }
        }

        string ServerGetString() {
            return ServerGetString("&");
        }

        string ServerGetString(string start) {
            return start + $"sip={server.Address}&sn={server.Name}&sp={server.Port}&an={config.ServerAdminName}&ae={config.ServerAdminEmail}&auid={config.OwnerSteamId}&gameId={covalence.ClientAppId}&gameName={covalence.Game}";
        }
        /*
                bool AssignGroupsAndBan(IPlayer player) {
                    try {
                        ISAPlayer isaPlayer = IsPlayerCached(player.Id) ? GetPlayerCache(player.Id) : null;
                        if (isaPlayer == null) return false;

                        if (config.AutoKickOn && ShouldBan(isaPlayer)) return ShouldBan(isaPlayer);


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
                }*/
        #endregion

        #region Kicking 

        void KickPlayer(string steamid, string reason) {
            IPlayer player = players.FindPlayerById(steamid);
            if (player.IsConnected) player?.Kick(reason);
        }

        bool ContainsMyBan(string steamid) {
            return IsBanned(steamid) != null;
        }

        bool IsVpn(ISAPlayer isaPlayer) {
            return (config.AutoKick_BadIp && isaPlayer.ipRating > 0.98);
        }

        ISABan IsBanned(string steamid) {
            if (steamid == null || steamid.Equals("0")) return null;
            if (!IsPlayerCached(steamid)) return null;
            try {
                return GetPlayerCache(steamid)?.serverBanData.First(x => x.serverIp.Equals(server.Address));
            } catch (InvalidOperationException ioe) {
                return null;
            }
        }

        bool IsLenderDirty(string steamid) {
            string lendersteamid = GetFamilyShareLenderSteamId(steamid);
            return lendersteamid != "0" ? IsPlayerDirty(lendersteamid) : false;
        }

        bool HasRecentVac(string playerid) {
            if (!IsPlayerCached(playerid)) return false;
            ISASteamData cache = GetPlayerCache(playerid).steamData;
            return (cache.VACBanned > 0 || cache.NumberOfVACBans > 0) ? cache.DaysSinceLastBan < config.DissallowVacBanDays : false;
        }

        bool HasReachedVacCeiling(ISAPlayer isaPlayer) {
            return config.AutoKickOn && (config.AutoVacBanCeiling <= isaPlayer.steamData.NumberOfVACBans || config.AutoKickCeiling < isaPlayer.serverBanCount);
        }

        bool IsFamilyShare(string steamid) {
            ISAPlayer player = GetPlayerCache(steamid);
            return !player.lendersteamid.Equals("0");
        }

        bool HasKeywordBan(ISAPlayer isaPlayer) {
            bool keywordBan = false;
            bool keywordBanCheck = config.AutoKickOn && (config.AutoKick_Reason_Keyword_Aimbot || config.AutoKick_Reason_Keyword_Cheat || config.AutoKick_Reason_Keyword_EspHack || config.AutoKick_Reason_Keyword_Hack || config.AutoKick_Reason_Keyword_Insult || config.AutoKick_Reason_Keyword_Ping || config.AutoKick_Reason_Keyword_Racism || config.AutoKick_Reason_Keyword_Script || config.AutoKick_Reason_Keyword_Toxic);
            if (keywordBanCheck) {
                foreach (ISABan ban in isaPlayer.serverBanData) {
                    if (config.AutoKick_Reason_Keyword_Aimbot && ban.isAimbot) keywordBan = true;
                    if (config.AutoKick_Reason_Keyword_Cheat && ban.isCheat) keywordBan = true;
                    if (config.AutoKick_Reason_Keyword_EspHack && ban.isEspHack) keywordBan = true;
                    if (config.AutoKick_Reason_Keyword_Hack && ban.isHack) keywordBan = true;
                    if (config.AutoKick_Reason_Keyword_Insult && ban.isInsult) keywordBan = true;
                    if (config.AutoKick_Reason_Keyword_Ping && ban.isPing) keywordBan = true;
                    if (config.AutoKick_Reason_Keyword_Racism && ban.isRacism) keywordBan = true;
                    if (config.AutoKick_Reason_Keyword_Script && ban.isScript) keywordBan = true;
                    if (config.AutoKick_Reason_Keyword_Toxic && ban.isToxic) keywordBan = true;
                    if (keywordBan) return true;
                }
            }
            return false;
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
            if (rpls != null) foreach (var rpl in rpls) message = message.Replace($"{{{rpl.Key}}}", rpl.Value);
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
                ["Reason: VAC Ban Too Fresh"] = "VAC ban received {daysago}, wait another {daysto}",
                ["Lender Banned"] = "The lender account contained a ban",
                ["Keyword Kick"] = "Due to your past behaviour on other servers, you aren't allowed in."
            }, this);
        }

        bool HasPermission(IPlayer player, string permissionName) {
            if (player.IsAdmin) return true;
            return permission.UserHasPermission(player.Id, permissionName);
        }
        #endregion

        #region Plugins methods
        string GetChatTag() => "[<#008080ff][Server Armour]:[/#] ";
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
        private static uint ConvertToTimestamp(string value) {
            return ConvertToTimestamp(ConverToDateTime(value));
        }
        private static uint ConvertToTimestamp(DateTime value) {
            TimeSpan elapsedTime = value - Epoch;
            return (uint)elapsedTime.TotalSeconds;
        }
        private static DateTime ConverToDateTime(string stringDate) {
            return DateTime.ParseExact(stringDate, DATE_FORMAT, null);
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
            public string address { get; set; }

            public ISAPlayer CreatePlayer(IPlayer bPlayer) {
                steamid = bPlayer.Id;
                username = bPlayer.Name;
                address = bPlayer.Address;
                cacheTimestamp = new Time().GetUnixTimestamp();
                lendersteamid = "0";
                lastConnected = new Time().GetUnixTimestamp();
                lenderSteamData = new ISASteamData();
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
            public string banUntil;
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
            public string AutoKickGroup; // the group name that banned users should be added in
            public bool AutoKickOn; // turn auto banning on or off. 
            public int AutoKickCeiling; // Auto ban players with X amount of previous bans.
            public int AutoVacBanCeiling; //  Auto ban players with X amount of vac bans.
            public int DissallowVacBanDays; // users who have been vac banned in this amount of days will not be allowed to connect. 

            public bool AutoKickFamilyShare;
            public bool AutoKickFamilyShareIfDirty;

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

            public bool AutoKick_Reason_Keyword_Aimbot;
            public bool AutoKick_Reason_Keyword_Hack;
            public bool AutoKick_Reason_Keyword_EspHack;
            public bool AutoKick_Reason_Keyword_Script;
            public bool AutoKick_Reason_Keyword_Cheat;
            public bool AutoKick_Reason_Keyword_Toxic;
            public bool AutoKick_Reason_Keyword_Insult;
            public bool AutoKick_Reason_Keyword_Ping;
            public bool AutoKick_Reason_Keyword_Racism;

            public bool AutoKick_BadIp;

            public string DiscordWebhookURL;
            public bool DiscordOnlySendDirtyReports;
            public bool SubmitArkanData;

            public string OwnerSteamId;
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





        #region Configuration

        private class SAConfig {
            // Config default vars
            public bool Debug = false;
            public bool ShowProtectedMsg = true;

            public string AutoKickGroup = "serverarmour.bans";
            public string WhitelistGroup = "serverarmour.whitelist";
            public string WatchlistGroup = "serverarmour.watchlist";

            public bool AutoKickOn = true;

            public int AutoKickCeiling = 5;
            public int AutoVacBanCeiling = 2;
            public int DissallowVacBanDays = 90;
            public int BroadcastPlayerBanReportVacDays = 120;

            public bool AutoKickFamilyShare = false;
            public bool AutoKickFamilyShareIfDirty = false;
            public int WatchlistCeiling = 1;
            public string BetterChatDirtyPlayerTag = string.Empty;
            public bool BroadcastPlayerBanReport = true;
            public bool BroadcastNewBans = true;
            public bool ServerAdminShareDetails = true;
            public string ServerAdminName = string.Empty;
            public string ServerAdminEmail = string.Empty;
            public string ServerApiKey = "FREE";

            public bool AutoKick_Reason_Keyword_Aimbot = false;
            public bool AutoKick_Reason_Keyword_Hack = false;
            public bool AutoKick_Reason_Keyword_EspHack = false;
            public bool AutoKick_Reason_Keyword_Script = false;
            public bool AutoKick_Reason_Keyword_Cheat = false;
            public bool AutoKick_Reason_Keyword_Toxic = false;
            public bool AutoKick_Reason_Keyword_Insult = false;
            public bool AutoKick_Reason_Keyword_Ping = false;
            public bool AutoKick_Reason_Keyword_Racism = false;
            public bool AutoKick_BadIp = false;

            public string DiscordWebhookURL = "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks";
            public bool DiscordOnlySendDirtyReports = true;
            public bool SubmitArkanData = true;

            public string OwnerSteamId = "";

            // Plugin reference
            private ServerArmour plugin;
            public SAConfig(ServerArmour plugin) {
                this.plugin = plugin;
                /**
                 * Load all saved config values
                 * */
                GetConfig(ref Debug, "");
                GetConfig(ref ShowProtectedMsg, "");
                GetConfig(ref AutoKickGroup, "");
                GetConfig(ref AutoKickOn, "");
                GetConfig(ref AutoKickCeiling, "");
                GetConfig(ref AutoVacBanCeiling, "");
                GetConfig(ref DissallowVacBanDays, "");
                GetConfig(ref AutoKickFamilyShare, "");
                GetConfig(ref AutoKickFamilyShareIfDirty, "");
                GetConfig(ref WatchlistGroup, "");
                GetConfig(ref WatchlistCeiling, "");
                GetConfig(ref BetterChatDirtyPlayerTag, "");
                GetConfig(ref BroadcastPlayerBanReport, "");
                GetConfig(ref BroadcastPlayerBanReportVacDays, "");
                GetConfig(ref BroadcastNewBans, "");
                GetConfig(ref ServerAdminShareDetails, "");
                GetConfig(ref ServerAdminName, "");
                GetConfig(ref ServerAdminEmail, "");
                GetConfig(ref ServerApiKey, "");
                GetConfig(ref AutoKick_Reason_Keyword_Aimbot, "");
                GetConfig(ref AutoKick_Reason_Keyword_Hack, "");
                GetConfig(ref AutoKick_Reason_Keyword_EspHack, "");
                GetConfig(ref AutoKick_Reason_Keyword_Script, "");
                GetConfig(ref AutoKick_Reason_Keyword_Cheat, "");
                GetConfig(ref AutoKick_Reason_Keyword_Toxic, "");
                GetConfig(ref AutoKick_Reason_Keyword_Insult, "");
                GetConfig(ref AutoKick_Reason_Keyword_Ping, "");
                GetConfig(ref AutoKick_Reason_Keyword_Racism, "");
                GetConfig(ref AutoKick_BadIp, "");
                GetConfig(ref DiscordWebhookURL, "");
                GetConfig(ref DiscordOnlySendDirtyReports, "");
                GetConfig(ref SubmitArkanData, "");
                GetConfig(ref OwnerSteamId, "");

                plugin.SaveConfig();
            }

            private void GetConfig<T>(ref T variable, params string[] path) {
                if (path.Length == 0) return;

                if (plugin.Config.Get(path) == null) {
                    SetConfig(ref variable, path);
                    plugin.PrintWarning($"Added new field to config: {string.Join("/", path)}");
                }

                variable = (T)Convert.ChangeType(plugin.Config.Get(path), typeof(T));
            }

            private void SetConfig<T>(ref T variable, params string[] path) => plugin.Config.Set(path.Concat(new object[] { variable }).ToArray());
        }

        protected override void LoadDefaultConfig() => PrintWarning("Generating new configuration file.");
        #endregion
    }
}
