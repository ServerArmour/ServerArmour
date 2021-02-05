using Newtonsoft.Json;

using ConVar;
using Facepunch;
using Newtonsoft.Json.Linq;

using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WebSocketSharp;
using Oxide.Game.Rust.Libraries;
using Time = Oxide.Core.Libraries.Time;

namespace Oxide.Plugins
{
    [Info("Server Armour", "Pho3niX90", "0.6.16")]
    [Description("Protect your server! Auto ban known hackers, scripters and griefer accounts, and notify server owners of threats.")]
    class ServerArmour : CovalencePlugin
    {
        #region Variables
        string api_hostname = "https://io.serverarmour.com"; // 
        Dictionary<string, ISAPlayer> _playerData = new Dictionary<string, ISAPlayer>();
        private Time _time = GetLibrary<Time>();
        private double cacheLifetime = 1; // minutes
        private SAConfig config;
        string specifier = "G";
        bool debug = false;
        CultureInfo culture = CultureInfo.CreateSpecificCulture("en-US");
        //StringComparison defaultCompare = StringComparison.InvariantCultureIgnoreCase;
        const string DATE_FORMAT = "yyyy/MM/dd HH:mm";
        const string DATE_FORMAT2 = "yyyy-MM-dd HH:mm:ss";
        ulong ServerArmourId = 76561199060671869L;
        bool init = false;
        private Dictionary<string, string> headers;
        string adminIds = "";

        private static ServerArmour _instance;
        #endregion

        #region Libraries
        private readonly Game.Rust.Libraries.Player Player = Interface.Oxide.GetLibrary<Game.Rust.Libraries.Player>();
        #endregion

        #region Permissions
        const string PermissionToBan = "serverarmour.ban";
        const string PermissionToUnBan = "serverarmour.unban";

        const string PermissionAdminWebsite = "serverarmour.website.admin";

        const string PermissionWhitelistRecentVacKick = "serverarmour.whitelist.recentvac";
        const string PermissionWhitelistBadIPKick = "serverarmour.whitelist.badip";
        const string PermissionWhitelistKeywordKick = "serverarmour.whitelist.keyword";
        const string PermissionWhitelistVacCeilingKick = "serverarmour.whitelist.vacceiling";
        const string PermissionWhitelistServerCeilingKick = "serverarmour.whitelist.banceiling";
        const string PermissionWhitelistGameBanCeilingKick = "serverarmour.whitelist.gamebanceiling";
        const string PermissionWhitelistSteamProfile = "serverarmour.whitelist.steamprofile";
        const string PermissionWhitelistFamilyShare = "serverarmour.whitelist.familyshare";
        const string PermissionWhitelistTwitterBan = "serverarmour.whitelist.twitterban";
        #endregion

        #region Plugins
        [PluginReference] Plugin DiscordApi, DiscordMessages, BetterChat, Ember, Clans;

        void DiscordSend(string steamId, string name, EmbedFieldList report, int color = 39423, bool isBan = false) {
            string webHook;
            if (isBan) {
                if (config.DiscordBanWebhookURL.Length == 0 || config.DiscordBanWebhookURL.Equals("https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks")) { Puts("Discord webhook not setup."); return; }
                webHook = config.DiscordBanWebhookURL;
            } else {
                if (config.DiscordWebhookURL.Length == 0 || config.DiscordWebhookURL.Equals("https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks")) { Puts("Discord webhook not setup."); return; }
                webHook = config.DiscordWebhookURL;
            }

            List<EmbedFieldList> fields = new List<EmbedFieldList>();
            if (config.DiscordQuickConnect) {
                fields.Add(new EmbedFieldList() {
                    name = server.Name,
                    value = $"[steam://connect/{config.ServerIp}:{server.Port}](steam://connect/{config.ServerIp}:{server.Port})",
                    inline = true
                });
            }

            fields.Add(new EmbedFieldList() {
                name = "Steam Profile",
                value = $"[{name}\n{steamId}](https://steamcommunity.com/profiles/{steamId})",
                inline = !config.DiscordQuickConnect
            });

            fields.Add(new EmbedFieldList() {
                name = "Server Armour Profile ",
                value = $"[{name}\n{steamId}](https://io.serverarmour.com/profile/{steamId})",
                inline = !config.DiscordQuickConnect
            });

            fields.Add(report);
            var fieldsObject = fields.Cast<object>().ToArray();
            string json = JsonConvert.SerializeObject(fieldsObject);

            if (DiscordApi != null && DiscordApi.IsLoaded) {
                DiscordApi?.Call("API_SendEmbeddedMessage", webHook, "Server Armour Report: ", color, json);
            } else if (DiscordMessages != null && DiscordMessages.IsLoaded) {
                DiscordMessages?.Call("API_SendFancyMessage", webHook, "Server Armour Report: ", color, json);
            } else {
                LogWarning("No discord API plugin loaded, will not publish to hook!");
            }
        }

        #endregion

        #region Hooks
        void OnServerInitialized(bool first) {
            _instance = this;
            config = new SAConfig(this);
            LoadData();

            // CheckOnlineUsers();
            // CheckLocalBans();

            Puts("Server Armour is being initialized.");
            string serverAddress = covalence.Server.Address.ToString();
            if (string.IsNullOrEmpty(config.ServerIp) && !string.IsNullOrEmpty(serverAddress) && !serverAddress.Equals("0.0.0.0")) {
                config.ServerIp = serverAddress;
            }
            if (string.IsNullOrEmpty(config.ServerGPort)) {
                config.ServerGPort = server.Port.ToString();
            }
            SaveConfig();
            Puts($"Server IP is {config.ServerIp} / {server.Address}");
            Puts($"Server Port is {server.Port}");

            RegPerm(PermissionToBan);
            RegPerm(PermissionToUnBan);

            RegPerm(PermissionAdminWebsite);

            RegPerm(PermissionWhitelistBadIPKick);
            RegPerm(PermissionWhitelistKeywordKick);
            RegPerm(PermissionWhitelistRecentVacKick);
            RegPerm(PermissionWhitelistServerCeilingKick);
            RegPerm(PermissionWhitelistVacCeilingKick);
            RegPerm(PermissionWhitelistGameBanCeilingKick);
            RegPerm(PermissionWhitelistSteamProfile);
            RegPerm(PermissionWhitelistFamilyShare);
            RegPerm(PermissionWhitelistTwitterBan);
            RegisterTag();

            headers = new Dictionary<string, string> {
                { "server_key", config.ServerApiKey },
                { "Accept", "application/json" }
            };

            string[] _admins = permission.GetUsersInGroup("admin");
            if (config.OwnerSteamId != null && config.OwnerSteamId.Length > 0 && !_admins.Contains(config.OwnerSteamId)) {
                var adminsList = new List<string>();
                adminsList.AddRange(_admins);
                adminsList.AddRange(config.OwnerSteamId.Split(','));

                var extraAdmins = permission.GetPermissionUsers(PermissionAdminWebsite);
                if (extraAdmins != null && extraAdmins.Length > 0) {
                    adminsList.AddRange(extraAdmins);
                }

                int e = 0;
                foreach (string extraAdminsGroups in permission.GetPermissionGroups(PermissionAdminWebsite)) {
                    foreach (string extraAdmin in permission.GetUsersInGroup(extraAdminsGroups)) {
                        if (!adminsList.Contains(extraAdmin)) {
                            adminsList.Add(extraAdmin);
                            e++;
                        }
                    }
                }

                Puts($"{(extraAdmins.Length + e)} admins found with website permission");


                _admins = adminsList.Distinct().ToArray();
            }

            for (int i = 0, n = _admins.Length; i < n; i++) {
                adminIds += $"{_admins[i].Substring(0, 17)}" + (i < _admins.Length - 1 ? "," : "");
            }

            CheckServerConnection();
        }

        void CheckServerConnection() {
            string body = ServerGetString();
            webrequest.Enqueue($"{api_hostname}/api/v1/plugin/check_server", body, (code, response) => {
                if (code > 204 || response.IsNullOrEmpty()) {
                    Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                    timer.Once(3, CheckServerConnection);
                    return;
                }

                JObject obj = null;

                try {
                    obj = JObject.Parse(response);
                } catch (Exception e) {
                    timer.Once(5, CheckServerConnection);
                    return;
                }

                if (obj != null) {
                    var msg = obj["message"].ToString();
                    var key = obj["key"]?.ToString();
                    if (msg.Equals("connected")) {
                        init = true;
                        Puts("connected to SA API");
                    } else if (msg.Equals("key assigned to server") || msg.Equals("key assigned to new server")) {
                        config.SetConfig(ref key, "io.serverarmour.com", "Server Key");

                        headers = new Dictionary<string, string> {
                        { "server_key", key },
                        { "Accept", "application/json" } };

                        SaveConfig();
                        Puts("Server key has been updated");
                        init = true;
                    } else {
                        LogError("Server Armour has not initialized. Is your apikey correct? Get it from https://io.serverarmour.com/my-servers or join discord for support https://discord.gg/jxvRaPR");
                        Interface.Oxide.UnloadPlugin(Name);
                        return;
                    }
                    Puts("Server Armour has initialized.");
                }

            }, this, RequestMethod.POST, headers);
        }

        void Unload() {
            SaveData();
            _playerData.Clear();
            _playerData = null;
        }

        //[Command("tc")]
        void OnUserConnected(IPlayer player) {

            if (!init) {
                LogError("User not checked. Server armour is not loaded.");
            } else {

                //lets check the userid first.
                if (config.AutoKick_KickWeirdSteam64 && !player.Id.IsSteamId()) {
                    KickPlayer(player.Id, GetMsg("Strange Steam64ID"), "C");
                    return;
                }

                GetPlayerBans(player);

                if (config.ShowProtectedMsg) SendReplyWithIcon(player, GetMsg("Protected MSG"));
            }
        }

        void OnUserDisconnected(IPlayer player) {
            if (init) {
                _playerData = _playerData.Where(pair => minutesAgo((uint)pair.Value.cacheTimestamp) < cacheLifetime)
                                 .ToDictionary(pair => pair.Key,
                                               pair => pair.Value);
            }
        }

        void OnPluginLoaded(Plugin plugin) {
            if (plugin.Title == "BetterChat") RegisterTag();
        }

        void OnUserUnbanned(string name, string id, string ipAddress) {
            SaUnban(id);
        }

        void OnUserBanned(string name, string id, string ipAddress, string reason) {
            //this is to make sure that if an app like battlemetrics for example, bans a player, we catch it.
            timer.Once(3f, () => {
                //lets make sure first it wasn't us. 
                if (!IsPlayerCached(id) && (IsPlayerCached(id) && !ContainsMyBan(id))) {
                    Puts($"Player wasn't banned via Server Armour, now adding to DB with a default lengh ban of 100yrs {name} ({id}) at {ipAddress} was banned: {reason}");
                    IPlayer bPlayer = players.FindPlayerById(id);
                    if (bPlayer != null) {
                        if ((bPlayer.IsAdmin && config.IgnoreAdmins)) return;

                        AddBan(bPlayer, new ISABan {
                            serverName = server.Name,
                            serverIp = config.ServerIp,
                            reason = reason,
                            created = DateTime.Now.ToString(DATE_FORMAT),
                            banUntil = DateTime.Now.AddYears(100).ToString(DATE_FORMAT)
                        }, false);
                    }
                }
            });
        }


        void OnPlayerReported(BasePlayer reporter, string targetName, string targetId, string subject, string message, string type) {
            string messageClean = Uri.EscapeDataString(message);
            string subjectClean = Uri.EscapeDataString(subject);
            webrequest.Enqueue($"{api_hostname}/api/v1/plugin/player/{reporter.UserIDString}/addf7",
                $"target={targetId}&subject={subjectClean}&message={messageClean}", (code, response) => {
                    if (code > 204 || response == null) {
                        Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                        return;
                    }
                }, this, RequestMethod.POST, headers);
        }
        #endregion

        #region API_Hooks

        #endregion

        #region WebRequests

        void GetPlayerBans(IPlayer player) {
            KickIfBanned(GetPlayerCache(player?.Id));
            _webCheckPlayer(player.Name, player.Id, player.Address, player.IsConnected);
        }

        void GetPlayerBans(string playerId, string playerName) {
            KickIfBanned(GetPlayerCache(playerId));
            _webCheckPlayer(playerName, playerId, "0.0.0.0", true);
        }

        void _webCheckPlayer(string name, string id, string address, bool connected) {
            string playerName = Uri.EscapeDataString(name);
            try {
                webrequest.Enqueue($"{api_hostname}/api/v1/plugin/player/{id}?bans=true", $"ipAddress={address}", (code, response) => {
                    if (code > 204 || response.IsNullOrEmpty()) {
                        if (code == 500) {
                            timer.Once(5, () => _webCheckPlayer(name, id, address, connected));
                        } else {
                            Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                        }
                        return;
                    }

                    LogDebug("Getting player from API");
                    ISAPlayer isaPlayer = null;


                    try {
                        isaPlayer = JsonConvert.DeserializeObject<ISAPlayer>(response);
                    } catch (Exception e) {
                        timer.Once(5, () => _webCheckPlayer(name, id, address, connected));
                        return;
                    }

                    isaPlayer.cacheTimestamp = _time.GetUnixTimestamp();
                    isaPlayer.lastConnected = _time.GetUnixTimestamp();


                    // add cache for player
                    LogDebug("Checking cache");
                    if (!IsPlayerCached(isaPlayer.steamid)) {
                        AddPlayerCached(isaPlayer);
                    } else {
                        UpdatePlayerData(isaPlayer);
                    }

                    // lets check bans first
                    try {
                        KickIfBanned(isaPlayer);
                    } catch (Exception ane) {
                        Puts("An ArgumentNullException occured. Please notify the developer along with the below information: ");
                        Puts(response);
                        Puts(ane.StackTrace);
                    }

                    //script vars
                    string pSteamId = isaPlayer.steamid;
                    string lSteamId = GetFamilyShare(isaPlayer.steamid);
                    //

                    LogDebug("Check for a twitter game ban");
                    if (config.AutoKick_KickTwitterGameBanned && !HasPerm(pSteamId, PermissionWhitelistTwitterBan)) {
                        if (isaPlayer.twitterBanId > 0) {
                            KickPlayer(isaPlayer?.steamid, $"https://twitter.com/rusthackreport/status/{isaPlayer.twitterBanId}", "C");
                        }
                    }

                    LogDebug("Check for a recent vac");
                    bool pRecentVac = (isaPlayer.steamNumberOfVACBans > 0 || isaPlayer.steamDaysSinceLastBan > 0)
                    && isaPlayer.steamDaysSinceLastBan < config.DissallowVacBanDays; //check main player

                    bool lRecentVac = (isaPlayer.lender?.steamNumberOfVACBans > 0 || isaPlayer.lender?.steamDaysSinceLastBan > 0)
                    && isaPlayer.lender?.steamDaysSinceLastBan < config.DissallowVacBanDays; //check the lender player

                    if (config.AutoKickOn && !HasPerm(pSteamId, PermissionWhitelistRecentVacKick) && (pRecentVac || lRecentVac)) {
                        int vacLast = pRecentVac ? isaPlayer.steamDaysSinceLastBan : isaPlayer.lender.steamDaysSinceLastBan;
                        int until = config.DissallowVacBanDays - vacLast;

                        Interface.CallHook("OnSARecentVacKick", vacLast, until);

                        string msg = GetMsg(pRecentVac ? "Reason: VAC Ban Too Fresh" : "Reason: VAC Ban Too Fresh - Lender", new Dictionary<string, string> { ["daysago"] = vacLast.ToString(), ["daysto"] = until.ToString() });

                        KickPlayer(isaPlayer?.steamid, msg, "C");
                    }

                    LogDebug("Check for a family share account");
                    if (!HasPerm(pSteamId, PermissionWhitelistFamilyShare) && config.AutoKickFamilyShare && IsFamilyShare(pSteamId)) {
                        Interface.CallHook("OnSAFamilyShareKick", pSteamId);
                        KickPlayer(isaPlayer?.steamid, GetMsg("Family Share Kick"), "C");
                    }

                    LogDebug("Check for too many vac bans");
                    if (!HasPerm(pSteamId, PermissionWhitelistVacCeilingKick) && HasReachedVacCeiling(isaPlayer)) {
                        Interface.CallHook("OnSATooManyVacKick", pSteamId, isaPlayer?.steamNumberOfVACBans);
                        KickPlayer(isaPlayer?.steamid, GetMsg("VAC Ceiling Kick"), "C");
                    }

                    LogDebug("Check for too many game bans");
                    if (!HasPerm(pSteamId, PermissionWhitelistGameBanCeilingKick) && HasReachedGameBanCeiling(isaPlayer)) {
                        Interface.CallHook("OnSATooManyGameBansKick", pSteamId, isaPlayer.steamNumberOfGameBans);
                        KickPlayer(isaPlayer?.steamid, GetMsg("Too Many Previous Game Bans"), "C");
                    }

                    /*LogDebug("Check for bloody/a4tech owner");
                    if (!HasPerm(pSteamId, PermissionWhitelistHardwareOwnsBloody) && (OwnsBloody(isaPlayer) || HasGroup(pSteamId, GroupBloody))) {
                        Interface.CallHook("OnSABloodyKick", pSteamId);
                        KickPlayer(isaPlayer?.steamid, GetMsg("Kick Bloody"), "C");
                    }*/

                    LogDebug("Check for players with too many bans");
                    if (!HasPerm(pSteamId, PermissionWhitelistServerCeilingKick) && HasReachedServerCeiling(isaPlayer)) {
                        Interface.CallHook("OnSATooManyBans", pSteamId, config.AutoKickCeiling, ServerBanCount(isaPlayer) + ServerBanCount(isaPlayer.lender));
                        KickPlayer(isaPlayer?.steamid, GetMsg("Too Many Previous Bans"), "C");
                    }

                    LogDebug("Check for players with a private profile");
                    if (!HasPerm(pSteamId, PermissionWhitelistSteamProfile) && config.AutoKick_KickPrivateProfile && isaPlayer.communityvisibilitystate == 1) {
                        Interface.CallHook("OnSAProfilePrivate", pSteamId, isaPlayer.communityvisibilitystate);
                        KickPlayer(isaPlayer?.steamid, GetMsg("Profile Private"), "C");
                    }

                    LogDebug("Check for a hidden steam level");
                    if (!HasPerm(pSteamId, PermissionWhitelistSteamProfile) && isaPlayer.steamlevel == -1 && config.AutoKick_KickHiddenLevel) {
                        Interface.CallHook("OnSASteamLevelHidden", pSteamId);
                        KickPlayer(isaPlayer?.steamid, GetMsg("Steam Level Hidden"), "C");
                    }

                    LogDebug("Check for low level steam account");
                    Puts($"Player {isaPlayer.steamid} is at steam level {isaPlayer.steamlevel}");
                    if (!HasPerm(pSteamId, PermissionWhitelistSteamProfile) && isaPlayer.steamlevel < config.AutoKick_MinSteamProfileLevel && isaPlayer.steamlevel >= 0) {
                        Interface.CallHook("OnSAProfileLevelLow", pSteamId, config.AutoKick_MinSteamProfileLevel, isaPlayer.steamlevel);
                        KickPlayer(isaPlayer?.steamid, GetMsg("Profile Low Level", new Dictionary<string, string> { ["level"] = config.AutoKick_MinSteamProfileLevel.ToString() }), "C");
                    }

                    LogDebug("Check for VPN");
                    Puts($"IP/CACHE| ID:{id} ADD:{address} RATING:{isaPlayer.ipRating} AGE:{isaPlayer.ipLastCheck}");
                    if (config.AutoKickOn && config.AutoKick_BadIp && !HasPerm(id, PermissionWhitelistBadIPKick) && config.AutoKick_BadIp) {
                        if (IsBadIp(isaPlayer)) {
                            if (isaPlayer.ipInfo.proxy == "yes")
                                KickPlayer(id, GetMsg("Reason: Proxy IP"), "C");
                            else
                                KickPlayer(id, GetMsg("Reason: Bad IP"), "C");

                            Interface.CallHook("OnSAVPNKick", id, isaPlayer.ipRating);
                        }
                    }

                    GetPlayerReport(isaPlayer, connected);
                }, this, RequestMethod.POST, headers);
            } catch (ArgumentNullException ice) {
                Puts("An ArgumentNullException occured. Please notify the developer along with the below information: ");
                Puts(ice.Message);
                return;
            }
        }

        void KickIfBanned(ISAPlayer isaPlayer) {
            if (isaPlayer == null) return;
            IPlayer iPlayer = covalence.Players.FindPlayer(isaPlayer.steamid);
            if (iPlayer != null && iPlayer.IsAdmin && config.IgnoreAdmins) return;

            LogDebug("KIB 1");
            ISABan ban = IsBanned(isaPlayer?.steamid);
            LogDebug("KIB 2");
            ISABan lban = IsBanned(isaPlayer?.lender?.steamid);
            LogDebug("KIB 3");
            if (ban != null) {
                KickPlayer(isaPlayer?.steamid, ban.reason, "U");
            }
            if (lban != null) KickPlayer(isaPlayer?.steamid, GetMsg("Lender Banned"), "U");
        }

        void AddBan(IPlayer player, ISABan thisBan, bool doNative = true) {
            if (config.IgnoreAdmins && player.IsAdmin) return;
            if (thisBan == null) return;
            string reason = Uri.EscapeDataString(thisBan.reason);

            webrequest.Enqueue($"{api_hostname}/api/v1/plugin/player/{player.Id}/addban",
                $"ip={player?.Address}&reason={reason}&dateTime={thisBan.created}&dateUntil={thisBan.banUntil}", (code, response) => {
                    if (code > 204) {
                        if (code == 500) {
                            timer.Once(5, () => AddBan(player, thisBan, doNative = true));
                        } else {
                            Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                        }
                        return;
                    }
                    // ISABan thisBan = new ISABan { serverName = server.Name, date = dateTime, reason = banreason, serverIp = thisServerIp, banUntil = dateBanUntil };
                    if (IsPlayerCached(player.Id)) {
                        LogDebug($"{player.Id} has ban cached, now updating.");
                        AddPlayerData(player.Id, thisBan);
                    } else {
                        LogDebug($"{player.Id} had no ban data cached, now creating.");
                        ISAPlayer newPlayer = new ISAPlayer(player);
                        newPlayer.bans.Add(thisBan);
                        AddPlayerCached(player, newPlayer);
                    }
                }, this, RequestMethod.POST, headers);
        }

        void AddBan(string playerId, ISABan thisBan) {
            if (thisBan == null) {
                Puts($"This ban is null");
                return;
            }

            string reason = Uri.EscapeDataString(thisBan.reason);
            try {
                webrequest.Enqueue($"{api_hostname}/api/v1/plugin/player/{playerId}/addban", $"reason={reason}&dateTime={thisBan.created}&dateUntil={thisBan.banUntil}", (code, response) => {

                    if (code > 204) { Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response })); return; }
                    // ISABan thisBan = new ISABan { serverName = server.Name, date = dateTime, reason = banreason, serverIp = thisServerIp, banUntil = dateBanUntil };
                    if (IsPlayerCached(playerId)) {
                        LogDebug($"{playerId} has ban cached, now updating.");
                        AddPlayerData(playerId, thisBan);
                    } else {
                        LogDebug($"{playerId} had no ban data cached, now creating.");
                        ISAPlayer newPlayer = new ISAPlayer(playerId);
                        newPlayer.bans.Add(thisBan);
                        AddPlayerCached(newPlayer);
                    }
                    SaveData();
                }, this, RequestMethod.POST, headers);
            } catch (Exception ice) {
                Puts("An ArgumentNullException occured. Please notify the developer along with the below information: ");
                Puts(ice.Message);
                return;
            }
        }

        #endregion

        #region Commands
        [Command("sa.clb", "getreport")]
        void SCmdCheckLocalBans(IPlayer player, string command, string[] args) {
            CheckLocalBans();
        }
        [Command("unban", "playerunban", "sa.unban"), Permission(PermissionToUnBan)]
        void SCmdUnban(IPlayer player, string command, string[] args) {

            NativeUnban(args[0]);

            if (args == null || (args.Length > 2 || args.Length < 1)) {
                SendReplyWithIcon(player, GetMsg("UnBan Syntax"));
                return;
            }

            var reason = args.Length > 1 ? args[1] : null;
            SaUnban(args[0], player, reason);
        }

        void SilentBan(IPlayer iPlayer, TimeSpan timeSpan, string reason) {
            Unsubscribe(nameof(OnUserBanned));
            iPlayer.Ban(reason, timeSpan);
            Subscribe(nameof(OnUserBanned));
        }

        bool SilentUnban(IPlayer iPlayer) {
            bool unbanned = false;
            if (iPlayer != null && iPlayer.IsBanned) {
                iPlayer.Unban();
                unbanned = true;
            }
            return unbanned;
        }

        void NativeUnban(string playerId) {
            ulong playerIdLong = 0;
            if (!ulong.TryParse(playerId, out playerIdLong)) {
                Puts(string.Format("This doesn't appear to be a 64bit steamid: {0}", playerId));
                return;
            }

            Unsubscribe(nameof(OnUserUnbanned));
            IPlayer iPlayer = covalence.Players.FindPlayer(playerId);
            if (iPlayer != null || !SilentUnban(iPlayer)) {
                FallbackNative(playerIdLong);
            }
            Subscribe(nameof(OnUserUnbanned));
        }

        void FallbackNative(ulong playerIdLong) {
            ServerUsers.User user = ServerUsers.Get(playerIdLong);
            if (user == null || user.@group != ServerUsers.UserGroup.Banned) {
                return;
            }
            ServerUsers.Remove(playerIdLong);
        }

        void SaUnban(string playerId, IPlayer player = null, string reason = null) {

            IPlayer iPlayer = players.FindPlayer(playerId);
            NativeUnban(playerId);

            if (iPlayer == null) {
                GetMsg("Player Not Found", new Dictionary<string, string> { ["player"] = playerId }); return;
            }

            RemoveBans(iPlayer.Id);
            LogDebug($"Player {iPlayer?.Name} ({iPlayer.Id}) was unbanned by {player.Name} ({player.Id})");

            // Add ember support.
            if (Ember != null) {
                Ember?.Call("Unban", playerId, Player.FindById(player?.Id));
            }
            //

            webrequest.Enqueue($"{api_hostname}/api/v1/plugin/player/ban/pardon/{iPlayer.Id}", $"", (code, response) => {
                if (code > 204 || response == null) {
                    Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                    return;
                }
                if (code <= 204) {
                    if (config.RconBroadcast)
                        RCon.Broadcast(RCon.LogType.Chat, new Chat.ChatEntry {
                            Message = $"Player was unbanned by {player?.Name} ({player.Id})",
                            UserId = iPlayer.Id,
                            Username = iPlayer?.Name ?? "",
                            Time = Facepunch.Math.Epoch.Current
                        });

                    if (player != null && config.RconBroadcast) {
                        RCon.Broadcast(RCon.LogType.Chat, new Chat.ChatEntry {
                            Message = $"Player { iPlayer?.Name } ({ iPlayer.Id }) was unbanned",
                            UserId = player?.Id,
                            Username = player?.Name ?? "",
                            Time = Facepunch.Math.Epoch.Current
                        });
                    }
                    if (player != null && !player.IsServer) {
                        SendReplyWithIcon(player, $"Player { iPlayer?.Name } ({ iPlayer.Id }) was unbanned");
                    }
                }
            }, this, RequestMethod.POST, headers);
            string msgClean = "";

            if (reason.IsNullOrEmpty()) {
                msgClean = GetMsg("Player Now Unbanned Clean - NoReason", new Dictionary<string, string> { ["player"] = iPlayer.Name });
            } else {
                msgClean = GetMsg("Player Now Unbanned Clean - Reason", new Dictionary<string, string> { ["player"] = iPlayer.Name, ["reason"] = reason });
            }

            if (config.DiscordBanReport) {
                DiscordSend(playerId, iPlayer.Name, new EmbedFieldList() {
                    name = "Player Unbanned",
                    value = msgClean,
                    inline = true
                }, 3066993, true);
            }
        }

        [Command("ban", "playerban", "sa.ban"), Permission(PermissionToBan)]
        void SCmdBan(IPlayer player, string command, string[] args) {
            int argsLength = args == null ? 0 : args.Length;
            /***
             * Length 2: player, reason
             * Length 3: player, reason, time
             * Length 4: playerSteamId, reason, time, ignoreSearch
             ***/
            Puts(args.Length.ToString());
            if (args == null || (argsLength > 4)) {
                SendReplyWithIcon(player, GetMsg("Ban Syntax"));
                return;
            }

            var playerId = args[0];
            var reason = argsLength < 2 ? "No reason provided." : args[1];
            var length = args.Length > 2 ? args[2].ToUpper() : "100Y";
            var ignoreSearch = false;

            if (args.Length > 3)
                bool.TryParse(args[3], out ignoreSearch);

            try {
                API_BanPlayer(player, playerId, reason, length, ignoreSearch);
            } catch (Exception e) {
                SendReplyWithIcon(player, GetMsg("Ban Syntax"));
            }
        }

        [Command("clanban"), Permission(PermissionToBan)]
        void SCmdClanBan(IPlayer player, string command, string[] args) {
            int argsLength = args == null ? 0 : args.Length;
            if (Clans == null || !Clans.IsLoaded) {
                SendReplyWithIcon(player, "Clans is either not installed, or loaded.");
                return;
            }
            /***
             * Length 2: player, reason
             * Length 3: player, reason, time
             * Length 4: playerSteamId, reason, time, ignoreSearch
             ***/
            if (args == null || (argsLength < 2 || argsLength > 4)) {
                SendReplyWithIcon(player, GetMsg("Ban Syntax"));
                return;
            }

            var playerId = args[0];
            var reason = args[1];
            var length = args.Length > 2 ? args[2] : "100y";
            var ignoreSearch = false;


            var errMsg = "";
            IPlayer iPlayer = null;
            IEnumerable<IPlayer> playersFound = players.FindPlayers(playerId);
            int playersFoundCount = playersFound.Count();
            switch (playersFoundCount) {
                case 0:
                    errMsg = GetMsg("Player Not Found", new Dictionary<string, string> { ["player"] = playerId });
                    break;
                case 1:
                    iPlayer = players.FindPlayer(playerId);
                    break;
                default:
                    List<string> playersFoundNames = new List<string>();
                    for (int i = 0; i < playersFoundCount; i++) playersFoundNames.Add(playersFound.ElementAt(i).Name);
                    string playersFoundNamesString = String.Join(", ", playersFoundNames.ToArray());
                    errMsg = GetMsg("Multiple Players Found", new Dictionary<string, string> { ["players"] = playersFoundNamesString });
                    break;
            }
            if ((!ignoreSearch && iPlayer == null) || !errMsg.Equals("")) { SendReplyWithIcon(player, errMsg); return; }

            ulong playerIdU = ulong.Parse(iPlayer.Id);

            List<ulong> teamMembers = new List<ulong>();
            if (config.ClanBanTeams) {
                teamMembers = GetTeamMembers(playerIdU);
            }

            var clanMembers = GetClan(playerId);
            if (clanMembers != null && clanMembers.Count() > 0) {
                teamMembers.AddRange(clanMembers);
            }

            teamMembers = teamMembers.Distinct().ToList();
            teamMembers?.Remove(ulong.Parse(playerId));

            try {
                ignoreSearch = bool.Parse(args[3]);
            } catch (Exception e) {

            }

            API_BanPlayer(player, playerId, reason, length, ignoreSearch);

            if (teamMembers.Count() > 0) {
                var clanBanReason = config.ClanBanPrefix.Replace("{reason}", reason).Replace("{playerId}", playerId);

                foreach (var member in teamMembers) {
                    API_BanPlayer(player, member.ToString(), clanBanReason, length, ignoreSearch);
                }
            }
        }

        [Command("sa.cp")]
        void SCmdCheckPlayer(IPlayer player, string command, string[] args) {
            string playerArg = (args.Length == 0) ? player.Id : args[0];

            IPlayer playerToCheck = players.FindPlayer(playerArg.Trim());
            if (playerToCheck == null) {
                SendReplyWithIcon(player, GetMsg("Player Not Found", new Dictionary<string, string> { ["player"] = playerArg }));
                return;
            }

            GetPlayerReport(playerToCheck, player);
        }
        #endregion

        #region VPN/Proxy
        #endregion

        #region Ban System

        string BanMinutes(DateTime ban) {
            return ((int)Math.Round((ban - DateTime.UtcNow).TotalMinutes)).ToString();
        }
        DateTime _BanUntil(string banLength) {
            int digit = int.Parse(new string(banLength.Where(char.IsDigit).ToArray()));
            string del = new string(banLength.Where(char.IsLetter).ToArray());
            if (digit <= 0) {
                digit = 100;
            }

            DateTime now = DateTime.UtcNow;
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
                    dateBanUntil = now.AddMonths(digit);
                    break;
                case "Y":
                    dateBanUntil = now.AddYears(Math.Min(2030 - DateTime.Now.Year, digit));
                    break;
                default:
                    dateBanUntil = now.AddDays(digit);
                    break;
            }
            return dateBanUntil;
        }

        string BanFor(string banLength) {
            int digit = int.Parse(new string(banLength.Where(char.IsDigit).ToArray()));
            string del = new string(banLength.Where(char.IsLetter).ToArray());


            DateTime now = DateTime.Now;
            string dateTime = now.ToString(DATE_FORMAT);
            string dateBanUntil;

            switch (del.ToUpper()) {
                case "MI":
                    dateBanUntil = digit + " Minutes";
                    break;
                case "H":
                    dateBanUntil = digit + " Hours";
                    break;
                case "D":
                    dateBanUntil = digit + " Days";
                    break;
                case "M":
                    dateBanUntil = digit + " Months";
                    break;
                case "Y":
                    dateBanUntil = digit + " Years";
                    break;
                default:
                    dateBanUntil = digit + " Days";
                    break;
            }
            return dateBanUntil;
        }

        void RemoveBans(string id) {
            if (_playerData.ContainsKey(id) && ServerBanCount(_playerData[id]) > 0) {
                _playerData[id].bans.RemoveAll(x => x.serverIp == config.ServerIp || x.adminSteamId.Equals(config.OwnerSteamId));
                SaveData();
            }
        }

        bool BanPlayer(string steamid, ISABan ban) {
            AddBan(steamid, ban);
            KickPlayer(steamid, ban.reason, "U");
            return true;
        }
        #endregion

        #region IEnumerators

        void CheckOnlineUsers() {
            // todo, improve by changing to single call.
            IEnumerable<IPlayer> allPlayers = players.Connected;

            int allPlayersCount = allPlayers.Count();

            List<string> playerCalls = new List<string>();

            int page = 0;
            for (int i = 0; i < allPlayersCount; i++) {
                if (i % 50 == 0) {
                    page++;
                }
                playerCalls[page] += allPlayers.ElementAt(i);
            }

            int allPlayersCounter = 0;
            float waitTime = 5f;
            if (allPlayersCount > 0)
                timer.Repeat(waitTime, allPlayersCount, () => {
                    LogDebug("Will now inspect all online users, time etimation: " + (allPlayersCount * waitTime) + " seconds");
                    LogDebug($"Inpecting online user {allPlayersCounter + 1} of {allPlayersCount} for infractions");
                    try {
                        IPlayer player = allPlayers.ElementAt(allPlayersCounter);
                        if (player != null) GetPlayerBans(player);
                        if (allPlayersCounter < allPlayersCount) LogDebug("Inspection completed.");
                        allPlayersCounter++;
                    } catch (ArgumentOutOfRangeException aore) {
                        allPlayersCounter++;
                    }
                });
        }

        void CheckLocalBans() {

            IEnumerable<ServerUsers.User> bannedUsers = ServerUsers.GetAll(ServerUsers.UserGroup.Banned);
            int BannedUsersCount = bannedUsers.Count();
            int BannedUsersCounter = 0;
            float waitTime = 1f;

            if (BannedUsersCount > 0)
                timer.Repeat(waitTime, BannedUsersCount, () => {
                    ServerUsers.User usr = bannedUsers.ElementAt(BannedUsersCounter);
                    LogDebug($"Checking local user ban {BannedUsersCounter + 1} of {BannedUsersCount}");
                    if (IsBanned(usr.steamid.ToString(specifier, culture)) == null && !usr.IsExpired) {
                        try {
                            IPlayer player = covalence.Players.FindPlayer(usr.steamid.ToString(specifier, culture));
                            DateTime expireDate = ConvertUnixToDateTime(usr.expiry);
                            if (expireDate.Year <= 1970) {
                                expireDate = expireDate.AddYears(100);
                            }
                            Puts($"Adding ban for {((player == null) ? usr.steamid.ToString() : player.Name)} with reason `{usr.notes}`, and expiry {expireDate.ToString(DATE_FORMAT)} to server armour.");
                            if (player != null) {
                                AddBan(player, new ISABan {
                                    serverName = server.Name,
                                    serverIp = config.ServerIp,
                                    reason = usr.notes,
                                    created = DateTime.Now.ToString(DATE_FORMAT),
                                    banUntil = expireDate.ToString(DATE_FORMAT)
                                });
                            } else {
                                AddBan(usr.steamid.ToString(), new ISABan {
                                    serverName = server.Name,
                                    serverIp = config.ServerIp,
                                    reason = usr.notes,
                                    created = DateTime.Now.ToString(DATE_FORMAT),
                                    banUntil = expireDate.ToString(DATE_FORMAT)
                                });
                            }
                        } catch (ArgumentOutOfRangeException aore) {
                            BannedUsersCounter++;
                        }
                    }

                    BannedUsersCounter++;
                });

        }
        #endregion

        #region Data Handling
        string GetFamilyShareLenderSteamId(string steamid) {
            return GetPlayerCache(steamid)?.lender?.steamid;
        }

        bool IsPlayerDirty(string steamid) {
            ISAPlayer isaPlayer = GetPlayerCache(steamid);
            return isaPlayer != null && IsPlayerCached(steamid) && (ServerBanCount(isaPlayer) > 0 || isaPlayer?.steamCommunityBanned > 0 || isaPlayer?.steamNumberOfGameBans > 0 || isaPlayer?.steamVACBanned > 0);
        }

        bool IsPlayerCached(string steamid) { return _playerData != null && _playerData.Count() > 0 && _playerData.ContainsKey(steamid); }
        void AddPlayerCached(ISAPlayer isaplayer) => _playerData.Add(isaplayer.steamid, isaplayer);
        void AddPlayerCached(IPlayer iplayer, ISAPlayer isaplayer) => _playerData.Add(iplayer.Id, isaplayer);
        ISAPlayer GetPlayerCache(string steamid) {
            return IsPlayerCached(steamid) ? _playerData[steamid] : null;
        }
        List<ISABan> GetPlayerBanData(string steamid) => _playerData[steamid].bans;
        int GetPlayerBanDataCount(string steamid) => ServerBanCount(_playerData[steamid]);
        void UpdatePlayerData(ISAPlayer isaplayer) => _playerData[isaplayer.steamid] = isaplayer;
        void AddPlayerData(string id, ISABan isaban) => _playerData[id].bans.Add(isaban);

        bool IsCacheValid(string id) {
            if (!_playerData.ContainsKey(id)) return false;
            return minutesAgo((uint)_playerData[id].cacheTimestamp) < cacheLifetime;
        }

        bool dateIsPast(DateTime to) {
            return DateTime.UtcNow > to;
        }

        double minutesAgo(uint to) {
            return Math.Round((_time.GetUnixTimestamp() - to) / 60.0);
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
            if (isaPlayer == null) return;
            Dictionary<string, string> data =
                       new Dictionary<string, string> {
                           ["status"] = IsPlayerDirty(isaPlayer.steamid) ? "dirty" : "clean",
                           ["steamid"] = isaPlayer.steamid,
                           ["username"] = isaPlayer.personaname,
                           ["serverBanCount"] = ServerBanCount(isaPlayer).ToString(),
                           ["NumberOfGameBans"] = isaPlayer.steamNumberOfGameBans.ToString(),
                           ["NumberOfVACBans"] = isaPlayer.steamNumberOfVACBans.ToString() + (isaPlayer.steamNumberOfVACBans > 0 ? $" Last({isaPlayer.steamDaysSinceLastBan}) days ago" : ""),
                           ["EconomyBan"] = (!isaPlayer.steamEconomyBan.Equals("none")).ToString(),
                           ["FamShare"] = IsFamilyShare(isaPlayer.steamid).ToString() + (IsFamilyShare(isaPlayer.steamid) ? IsLenderDirty(isaPlayer.steamid) ? "DIRTY" : "CLEAN" : "")
                       };

            if (IsPlayerDirty(isaPlayer.steamid) || isCommand) {
                string report = GetMsg("User Dirty MSG", data);
                if (config.BroadcastPlayerBanReport && isConnected && isCommand && !(config.BroadcastPlayerBanReportVacDays > isaPlayer.steamDaysSinceLastBan)) {
                    BroadcastWithIcon(report.Replace(isaPlayer.steamid + ":", string.Empty).Replace(isaPlayer.steamid, string.Empty));
                }
                if (isCommand) {
                    SendReplyWithIcon(cmdPlayer, report.Replace(isaPlayer.steamid + ":", string.Empty).Replace(isaPlayer.steamid, string.Empty));
                }
            }

            if ((config.DiscordOnlySendDirtyReports && IsPlayerDirty(isaPlayer.steamid)) || !config.DiscordOnlySendDirtyReports) {
                IPlayer iPlayer = players.FindPlayer(isaPlayer.steamid);
                DiscordSend(iPlayer.Id, iPlayer.Name, new EmbedFieldList() {
                    name = "Report",
                    value = GetMsg("User Dirty DISCORD MSG", data),
                    inline = true
                });

                if (config.RconBroadcast)
                    RCon.Broadcast(RCon.LogType.Chat, new Chat.ChatEntry {
                        Message = GetMsg("User Dirty DISCORD MSG", data),
                        UserId = isaPlayer.steamid,
                        Username = isaPlayer.personaname,
                        Time = Facepunch.Math.Epoch.Current
                    });

            }
        }

        private void LogDebug(string txt) {
            if (config.Debug || debug) Puts(txt);
        }

        void LoadData() {
            Dictionary<string, ISAPlayer> playerData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, ISAPlayer>>($"ServerArmour/playerData");
            if (playerData != null) {
                _playerData = playerData;
            }
        }

        void SaveData() {
            _playerData = _playerData.ToDictionary(pair => pair.Key, pair => pair.Value);
            Interface.Oxide.DataFileSystem.WriteObject($"ServerArmour/playerData", _playerData, true);
        }

        string ServerGetString() {
            string aname = Uri.EscapeDataString(config.ServerAdminName);
            string aemail = Uri.EscapeDataString(config.ServerAdminEmail);
            string owner = Uri.EscapeDataString(config.OwnerSteamId);
            string gport = Uri.EscapeDataString(config.ServerGPort);
            string qport = Uri.EscapeDataString(config.ServerQPort);
            string rport = Uri.EscapeDataString(config.ServerRPort);
            string sname = Uri.EscapeDataString(server.Name);
            return $"sip={config.ServerIp}&gport={gport}&qport={qport}&rport={rport}&ownerid={owner}&port={server.Port}&an={aname}&ae={aemail}&adminIds={adminIds}&gameId={covalence.ClientAppId}&gameName={covalence.Game}&v={this.Version}&sname={sname}";
        }
        #endregion

        #region Kicking 

        void KickPlayer(string steamid, string reason, string type) {
            IPlayer player = players.FindPlayerById(steamid);
            if (player == null || (player.IsAdmin && config.IgnoreAdmins)) return;

            if (player.IsConnected) {
                player?.Kick(reason);
                Puts($"Player {player?.Name} was kicked for `{reason}`");

                if (config.DiscordKickReport) {
                    DiscordSend(player.Id, player.Name, new EmbedFieldList() {
                        name = "Player Kicked",
                        value = reason,
                        inline = true
                    }, 13459797);
                }

                if (type.Equals("D") && config.BroadcastKicks) {
                    BroadcastWithIcon(GetMsg("Player Kicked", new Dictionary<string, string> { ["player"] = player.Name, ["reason"] = reason }));
                }
            }
        }

        bool IsBadIp(ISAPlayer isaPlayer) {
            if (isaPlayer.ipInfo == null) return false;

            return (config.AutoKick_BadIp
                && isaPlayer.ipInfo.type.ToLower() == "vpn" || isaPlayer.ipInfo.type.ToLower() == "proxy" || isaPlayer.ipInfo.proxy == "yes")
                && !(config.AutoKick_IgnoreNvidia && isaPlayer.ipInfo.isCloudComputing);
        }

        bool ContainsMyBan(string steamid) {
            return IsBanned(steamid) != null;
        }

        ISABan IsBanned(string steamid) {
            LogDebug("----------- is player banned?");
            if (steamid == null || steamid.Equals("0")) return null;
            LogDebug("is player cached?");
            if (!IsPlayerCached(steamid)) return null;
            LogDebug("player is cached!");
            try {
                ISAPlayer isaPlayer = GetPlayerCache(steamid);

                if (!config.OwnerSteamId.IsNullOrEmpty() && config.OwnerSteamId.StartsWith("7656") && config.AutoKick_NetworkBan) {
                    LogDebug("Check ban!");
                    return isaPlayer?.bans?.Count() > 0 ? isaPlayer?.bans?.FirstOrDefault(x => (x.serverIp.Equals(config.ServerIp)
                        || x.serverIp.Equals(covalence.Server.Address.ToString())
                        || (x.adminSteamId != null && x.adminSteamId.Contains(config.OwnerSteamId)))
                        && !dateIsPast(x.banUntillDateTime())) : null;

                }

                LogDebug("Nope!");


                return isaPlayer?.bans?.Count() > 0 ?
                    isaPlayer?.bans?.FirstOrDefault(x => x.serverIp.Equals(config.ServerIp)
                    && !dateIsPast(x.banUntillDateTime())) : null;

            } catch (InvalidOperationException ioe) {
                if (_playerData.ContainsKey(steamid)) {
                    _playerData.Remove(steamid);
                }
                Puts(ioe.Message);
                return null;
            }
        }

        bool IsLenderDirty(string steamid) {
            string lendersteamid = GetFamilyShareLenderSteamId(steamid);
            return lendersteamid != "0" ? IsPlayerDirty(lendersteamid) : false;
        }

        int ServerBanCount(ISAPlayer player) {
            if (player == null) return 0;
            try {
                return player.bans.Count();
            } catch (NullReferenceException) {
                return 0;
            }
        }

        bool HasReachedVacCeiling(ISAPlayer isaPlayer) {
            return config.AutoKickOn && config.AutoVacBanCeiling < (isaPlayer.steamNumberOfVACBans + (isaPlayer.lender != null ? isaPlayer.lender.steamNumberOfVACBans : 0));
        }

        bool HasReachedGameBanCeiling(ISAPlayer isaPlayer) {
            return config.AutoKickOn && config.AutoGameBanCeiling < (isaPlayer.steamNumberOfGameBans + isaPlayer.lender?.steamNumberOfGameBans);
        }

        /*bool OwnsBloody(ISAPlayer isaPlayer) {
            return config.AutoKickOn && config.AutoKick_Hardware_Bloody && isaPlayer.bloodyScriptsCount > 0;
        }*/

        bool HasReachedServerCeiling(ISAPlayer isaPlayer) {
            return config.AutoKickOn && config.AutoKickCeiling < (ServerBanCount(isaPlayer) + ServerBanCount(isaPlayer?.lender));
        }

        bool IsFamilyShare(string steamid) => !GetFamilyShare(steamid).IsNullOrEmpty();

        string GetFamilyShare(string steamid) {
            if (string.IsNullOrEmpty(steamid) || steamid.Length != 17) return null;
            var playerConn = BasePlayer.Find(steamid)?.Connection;
            return !playerConn.ownerid.ToString().Equals(steamid) ? playerConn.ownerid.ToString() : null;
        }

        bool IsProfilePrivate(string steamid) {
            ISAPlayer player = GetPlayerCache(steamid);
            return player.communityvisibilitystate == 1;
        }

        int GetProfileLevel(string steamid) {
            ISAPlayer player = GetPlayerCache(steamid);
            return (int)player.steamlevel;
        }

        #endregion

        #region API Hooks
        private int API_GetServerBanCount(string steamid) => IsPlayerCached(steamid) ? GetPlayerBanDataCount(steamid) : 0;
        private bool API_GetIsVacBanned(string steamid) => IsPlayerCached(steamid) ? GetPlayerCache(steamid).steamVACBanned == 1 : false;
        private bool API_GetIsCommunityBanned(string steamid) => IsPlayerCached(steamid) ? GetPlayerCache(steamid).steamCommunityBanned == 1 : false;
        private int API_GetVacBanCount(string steamid) => IsPlayerCached(steamid) ? GetPlayerCache(steamid).steamNumberOfVACBans : 0;
        private int API_GetDaysSinceLastVacBan(string steamid) => IsPlayerCached(steamid) ? GetPlayerCache(steamid).steamDaysSinceLastBan : 0;
        private int API_GetGameBanCount(string steamid) => IsPlayerCached(steamid) ? GetPlayerCache(steamid).steamNumberOfGameBans : 0;
        private string API_GetEconomyBanStatus(string steamid) => IsPlayerCached(steamid) ? GetPlayerCache(steamid).steamEconomyBan : "none";
        private bool API_GetIsPlayerDirty(string steamid) => IsPlayerDirty(steamid);
        private bool API_GetIsFamilyShareLenderDirty(string steamid) => IsLenderDirty(steamid);
        private bool API_GetIsFamilyShare(string steamid) => IsFamilyShare(steamid);
        private string API_GetFamilyShareLenderSteamId(string steamid) => GetFamilyShareLenderSteamId(steamid);
        private bool API_GetIsProfilePrivate(string steamid) => IsProfilePrivate(steamid);
        private int API_GetProfileLevel(string steamid) => GetProfileLevel(steamid);

        private void API_BanPlayer(IPlayer player, string playerNameId, string reason, string length = null, bool ignoreSearch = false) {

            /***
             * Length 2: player, reason
             * Length 3: player, reason, time
             * Length 4: playerSteamId, reason, time, ignoreSearch
             ***/
            string banPlayer = playerNameId; //0
            string banReason = reason; // 1
            ulong banSteamId = 0;

            DateTime now = DateTime.Now;
            string dateTime = now.ToString(DATE_FORMAT);
            /***
             * If time specified, default to 100 years
             ***/
            string lengthOfBan = !length.IsNullOrEmpty() ? length : "100Y";
            string dateBanUntil = _BanUntil(lengthOfBan).ToString(DATE_FORMAT);

            if (ignoreSearch) {
                try {
                    banSteamId = ulong.Parse(banPlayer);
                } catch (Exception e) {
                    SendReplyWithIcon(player, GetMsg("Ban Syntax"));
                    return;
                }
            }

            string errMsg = "";
            IPlayer iPlayer = null;

            if (!ignoreSearch) {
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
            }

            string playerId = ignoreSearch ? banSteamId.ToString() : iPlayer?.Id;
            string playerName = ignoreSearch ? banSteamId.ToString() : iPlayer?.Name;

            if ((!ignoreSearch && iPlayer == null) || !errMsg.Equals("")) { SendReplyWithIcon(player, errMsg); return; }

            ISAPlayer isaPlayer;

            if (!ignoreSearch) {
                if (!IsPlayerCached(playerId)) {
                    isaPlayer = new ISAPlayer(iPlayer);
                    AddPlayerCached(isaPlayer);
                } else {
                    isaPlayer = GetPlayerCache(banPlayer);
                }
            }


            if (BanPlayer(playerId,
                new ISABan {
                    serverName = server.Name,
                    serverIp = config.ServerIp,
                    reason = banReason,
                    created = dateTime,
                    banUntil = dateBanUntil
                })) {
                string msg;
                string banLengthText = lengthOfBan.Equals("100y") ? GetMsg("Permanent") : BanFor(lengthOfBan);

                msg = GetMsg("Player Now Banned Perma", new Dictionary<string, string> { ["player"] = playerName, ["reason"] = reason, ["length"] = banLengthText });
                string msgClean = GetMsg("Player Now Banned Clean", new Dictionary<string, string> { ["player"] = playerName, ["reason"] = reason, ["length"] = banLengthText });


                // Add ember support.
                if (Ember != null)
                    Ember?.Call("Ban", playerId, BanMinutes(_BanUntil(lengthOfBan)), banReason, true, config.OwnerSteamId, Player.FindById(player.Id));
                //

                if (config.RconBroadcast)
                    RCon.Broadcast(RCon.LogType.Chat, new Chat.ChatEntry {
                        Message = msgClean,
                        UserId = playerId,
                        Username = playerName,
                        Time = Facepunch.Math.Epoch.Current
                    });

                if (config.BroadcastNewBans) {
                    BroadcastWithIcon(msg);
                } else {
                    SendReplyWithIcon(player, msg);
                }

                if (config.DiscordBanReport) {
                    DiscordSend(playerId, playerName, new EmbedFieldList() {
                        name = "Player Banned",
                        value = msgClean,
                        inline = true
                    }, 13459797, true);
                }
                try {
                    SilentBan(BasePlayer.Find(playerId)?.IPlayer, TimeSpan.FromMinutes((_BanUntil(lengthOfBan) - DateTime.Now).TotalMinutes), banReason);
                } catch (Exception e) {
                    Subscribe(nameof(OnUserBanned));
                }
            }
        }
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
                ["Player Now Banned Perma"] = "[#ff0000]{player}[/#] has been banned\n[#ff0000]Reason:[/#] {reason}\n[#ff0000]Length:[/#] {length}",
                ["Player Now Banned Clean"] = "{player} has been banned\nReason: {reason}\nLength: {length}",
                ["Player Now Unbanned Clean - Reason"] = "{player} has been unbanned\nReason: {reason}",
                ["Player Now Unbanned Clean - NoReason"] = "{player} has been unbanned",
                ["Reason: Bad IP"] = "Bad IP Detected, either due to a VPN/Proxy",
                ["Reason: Proxy IP"] = "VPN & Proxy's not allowed.",
                ["Player Not Found"] = "Player wasn't found",
                ["Multiple Players Found"] = "Multiple players found with that name ({players}), please try something more unique like a steamid",
                ["Ban Syntax"] = "sa.ban <playerNameOrID> \"<reason>\" length (example: 1h for 1 hour, 1m for 1 month etc)",
                ["UnBan Syntax"] = "sa.unban <playerNameOrID> <reason>",
                ["No Response From API"] = "Couldn't get an answer from ServerArmour.com! Error: {code} {response}",
                ["Player Not Banned"] = "Player not banned",
                ["Broadcast Player Banned"] = "{tag} {username} wasn't allowed to connect\nReason: {reason}",
                ["Reason: VAC Ban Too Fresh"] = "VAC ban received {daysago} days ago, wait another {daysto} days",
                ["Reason: VAC Ban Too Fresh - Lender"] = "VAC ban received {daysago} days ago on lender account, wait another {daysto} days",
                ["Lender Banned"] = "The lender account contained a ban",
                ["Keyword Kick"] = "Due to your past behaviour on other servers, you aren't allowed in.",
                ["Family Share Kick"] = "Family share accounts are not allowed on this server.",
                ["Too Many Previous Bans"] = "You have too many previous bans (other servers included). Appeal in discord",
                ["Too Many Previous Game Bans"] = "You have too many previous game bans. Appeal in discord",
                ["Kick Bloody"] = "You own a bloody/a4 tech device. Appeal in discord",
                ["VAC Ceiling Kick"] = "You have too many VAC bans. Appeal in discord",
                ["Player Kicked"] = "[#ff0000]{player} Kicked[/#] - Reason\n{reason}",
                ["Profile Private"] = "Your Steam Profile is not allowed to be private on this server.",
                ["Profile Low Level"] = "You need a level {level} steam profile for this server.",
                ["Steam Level Hidden"] = "You are not allowed to hide your steam level on this server.",
                ["Strange Steam64ID"] = "Your steam id does not conform to steam standards.",
                ["Permanent"] = "Permanent"
            }, this, "en");
        }

        #endregion

        #region Plugins methods
        string GetChatTag() => "[#008080ff][Server Armour]:[/#] ";
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
        void SendReplyWithIcon(IPlayer player, string format, params object[] args) {
            int cnt = 0;
            string msg = GetMsg(format);
            foreach (var arg in args) {
                msg = msg.Replace("{" + cnt + "}", arg.ToString());
                cnt++;
            }

            if (!player.IsServer && player.IsConnected) {
                BasePlayer bPlayer = player.Object as BasePlayer;
                bPlayer?.SendConsoleCommand("chat.add", 2, ServerArmourId, FixColors(msg));
            } else {
                player?.Reply(msg);
            }
        }
        void BroadcastWithIcon(string format, params object[] args) {
            foreach (var player in BasePlayer.activePlayerList) {
                SendReplyWithIcon(player.IPlayer, format, args);
            }
        }

        string FixColors(string msg) => msg.Replace("[/#]", "</color>").Replace("[", "<color=").Replace("]", ">");

        void AddGroup(string group) {
            if (!permission.GroupExists(group)) permission.CreateGroup(group, "Bloody Mouse Owners", 0);
        }

        void AssignGroup(string id, string group) => permission.AddUserGroup(id, group);
        bool HasGroup(string id, string group) => permission.UserHasGroup(id, group);
        void RegPerm(string perm) {
            if (!permission.PermissionExists(perm)) permission.RegisterPermission(perm, this);
        }

        bool HasPerm(string id, string perm) => permission.UserHasPermission(id, perm);
        void GrantPerm(string id, string perm) => permission.GrantUserPermission(id, perm, this);

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static uint ConvertToTimestamp(string value) {
            return ConvertToTimestamp(ConverToDateTime(value));
        }

        private static uint ConvertToTimestamp(DateTime value) {
            TimeSpan elapsedTime = value - Epoch;
            return (uint)elapsedTime.TotalSeconds;
        }

        private static DateTime ConverToDateTime(string stringDate) {
            DateTime time;
            try {
                time = DateTime.ParseExact(stringDate, DATE_FORMAT, null);
            } catch (Exception e) {
                time = DateTime.ParseExact(stringDate.Replace("T", " ").Replace(".000Z", ""), DATE_FORMAT2, null);
            }
            _instance.Puts(time.ToString(DATE_FORMAT));
            return time;
        }

        private static DateTime ConvertUnixToDateTime(long unixTimeStamp) {
            // Unix timestamp is seconds past epoch
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }

        #endregion

        #region Classes 
        public class EmbedFieldList
        {
            public string name { get; set; }
            public string value { get; set; }
            public bool inline { get; set; }
        }

        public class ISAPlayer
        {
            public int id { get; set; }
            public string steamid { get; set; }
            public int? steamlevel { get; set; }

            public int steamCommunityBanned { get; set; }
            public int steamVACBanned { get; set; }
            public int steamNumberOfVACBans { get; set; }
            public int steamDaysSinceLastBan { get; set; }
            public string lastServerBan { get; set; }
            public int steamNumberOfGameBans { get; set; }
            public string steamEconomyBan { get; set; }
            public string steamBansLastCheckUTC { get; set; }
            public string created { get; set; }
            public string lastSeen { get; set; }
            public string lastBan { get; set; }
            public int rustadminBanCount { get; set; }
            public string rustadminLastCheck { get; set; }
            public float ipRating { get; set; }
            public string country { get; set; }
            public string ipLastCheck { get; set; }
            public int bloodyScriptsCount { get; set; }
            public int communityvisibilitystate { get; set; }
            public int? profilestate { get; set; }
            public string personaname { get; set; }
            public string avatar { get; set; }
            public string personastateflags { get; set; }
            public float avgProb { get; set; }
            public int aimbotViolations { get; set; }
            public int? steamDaysOld { get; set; }
            public long? twitterBanId { get; set; }
            public string twitterBanDate { get; set; }

            public uint? cacheTimestamp { get; set; }

            public uint? lastConnected { get; set; }
            public List<ISABan> bans { get; set; }
            public ISAPlayer lender { get; set; }

            public IPInfo ipInfo { get; set; }

            public ISAPlayer() {
            }
            public ISAPlayer(ulong steamId) {
                steamid = steamId.ToString();
                personaname = "";
            }
            public ISAPlayer(IPlayer bPlayer) {
                CreatePlayer(bPlayer);
            }
            public ISAPlayer(string id) {
                CreatePlayer(id);
            }

            public ISAPlayer CreatePlayer(IPlayer bPlayer) {
                steamid = bPlayer.Id;
                bloodyScriptsCount = 0;
                personaname = bPlayer.Name;
                cacheTimestamp = new Time().GetUnixTimestamp();
                lastConnected = new Time().GetUnixTimestamp();
                bans = new List<ISABan>();
                return this;
            }
            public ISAPlayer CreatePlayer(string id) {
                steamid = id;
                bloodyScriptsCount = 0;
                personaname = "";
                cacheTimestamp = new Time().GetUnixTimestamp();
                lastConnected = new Time().GetUnixTimestamp();
                bans = new List<ISABan>();
                return this;
            }
        }

        public class IPInfo
        {
            public string ip;
            public string lastcheck;
            public long longIp;
            public string asn;
            public string provider;
            public string continent;
            public string country;
            public string isocode;
            public string region;
            public string regioncode;
            public string city;
            public string latitude;
            public string longitude;
            public string proxy;
            public string type;
            public float rating;
            public bool isCloudComputing;
        }

        public class ISABan
        {
            public int id;
            public string adminSteamId;
            public long steamid;
            public string reason;
            public string banLength;
            public string serverName;
            public string serverIp;
            public string dateTime;
            public string created;
            public int gameId;
            public string banUntil;

            public uint GetUnixBanUntill() {
                return ConvertToTimestamp(banUntil);
            }
            public DateTime banUntillDateTime() {
                DateTime t;
                try {
                    t = DateTime.ParseExact(banUntil, DATE_FORMAT, null);
                } catch (Exception e) {
                    t = DateTime.ParseExact(banUntil.Replace("T", " ").Replace(".000Z", ""), DATE_FORMAT2, null);
                }
                return t;
            }
        }

        #endregion

        #region Arkan

        private void API_ArkanOnNoRecoilViolation(BasePlayer player, int NRViolationsNum, string jString) {
            if (jString != null) {
                JObject aObject = JObject.Parse(jString);

                string shotsCnt = aObject.GetValue("ShotsCnt").ToString();
                string violationProbability = aObject.GetValue("violationProbability").ToString();
                string ammoShortName = aObject.GetValue("ammoShortName").ToString();
                string weaponShortName = aObject.GetValue("weaponShortName").ToString();
                string attachments = String.Join(", ", aObject.GetValue("attachments").Select(jv => (string)jv).ToArray());
                string suspiciousNoRecoilShots = aObject.GetValue("suspiciousNoRecoilShots").ToString();

                webrequest.Enqueue($"{api_hostname}/api/v1/plugin/player/{player.UserIDString}/addarkan/nr",
                    $"vp={violationProbability}&sc={shotsCnt}&ammo={ammoShortName}&weapon={weaponShortName}&attach={attachments}&snrs={suspiciousNoRecoilShots}",
                    (code, response) => {
                        if (code > 204 || response == null) {
                            Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                            return;
                        }
                    }, this, RequestMethod.POST, headers);
            }
        }

        private void API_ArkanOnAimbotViolation(BasePlayer player, int AIMViolationsNum, string jString) {
            if (jString != null) {
                JObject aObject = JObject.Parse(jString);
                string attachments = String.Join(", ", aObject.GetValue("attachments").Select(jv => (string)jv).ToArray());
                string ammoShortName = aObject.GetValue("ammoShortName").ToString();
                string weaponShortName = aObject.GetValue("weaponShortName").ToString();
                string damage = aObject.GetValue("damage").ToString();
                string bodypart = aObject.GetValue("bodyPart").ToString();
                string hitsData = aObject.GetValue("hitsData").ToString();
                string hitInfoProjectileDistance = aObject.GetValue("hitInfoProjectileDistance").ToString();

                webrequest.Enqueue($"{api_hostname}/api/v1/plugin/player/{player.UserIDString}/addarkan/aim",
                    $"attach={attachments}&ammo={ammoShortName}&weapon={weaponShortName}&dmg={damage}&bp={bodypart}&distance={hitInfoProjectileDistance}&hits={hitsData}", (code, response) => {
                        if (code > 204 || response == null) {
                            Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                            return;
                        }
                    }, this, RequestMethod.POST, headers);
            }
        }

        #endregion

        #region Clan/Team Helpers
        List<ulong> GetTeamMembers(ulong userid) => RelationshipManager.Instance.FindPlayersTeam(userid)?.members;
        string GetClanTag(string userid) => Clans?.Call<string>("GetClanOf", userid);
        List<ulong> GetClan(string userid) => Clans?.Call<JObject>("GetClan", GetClanTag(userid))?.GetValue("members").ToObject<List<ulong>>();
        #endregion

        #region Configuration
        private class SAConfig
        {
            // Config default vars
            public string ServerIp = "";
            public string ServerGPort = "";
            public string ServerQPort = "";
            public string ServerRPort = "";
            public bool Debug = false;
            public bool ShowProtectedMsg = true;
            public bool AutoKickOn = true;
            public int AutoKickCeiling = 3;
            public int AutoVacBanCeiling = 1;
            public int AutoGameBanCeiling = 2;
            public int DissallowVacBanDays = 90;
            public int BroadcastPlayerBanReportVacDays = 120;

            public bool AutoKickFamilyShare = false;
            public bool AutoKickFamilyShareIfDirty = false;
            public string BetterChatDirtyPlayerTag = string.Empty;
            public bool BroadcastPlayerBanReport = true;
            public bool BroadcastNewBans = true;
            public bool BroadcastKicks = false;
            public bool ServerAdminShareDetails = true;
            public string ServerAdminName = string.Empty;
            public string ServerAdminEmail = string.Empty;
            public string ServerApiKey = string.Empty;

            //public bool AutoKick_Hardware_Bloody = true;
            public bool AutoKick_KickHiddenLevel = false;
            public int AutoKick_MinSteamProfileLevel = -1;
            public bool AutoKick_KickPrivateProfile = false;
            public bool AutoKick_KickWeirdSteam64 = true;

            public bool AutoKick_KickTwitterGameBanned = true;
            public bool AutoKick_BadIp = true;
            public bool AutoKick_BadIp_IgnoreComputing = true;

            public string DiscordWebhookURL = "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks";
            public string DiscordBanWebhookURL = "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks";
            public bool DiscordQuickConnect = true;
            public bool DiscordOnlySendDirtyReports = true;
            public bool DiscordKickReport = true;
            public bool DiscordBanReport = true;
            public bool DiscordNotifyGameBan = true;
            public bool SubmitArkanData = true;
            public bool RconBroadcast = false;
            public bool AutoKick_IgnoreNvidia = true;
            public bool AutoKick_NetworkBan = true;

            public string OwnerSteamId = "";
            public string ClanBanPrefix = "Assoc Ban -> {playerId}: {reason}";
            public bool ClanBanTeams = true;
            public bool IgnoreAdmins = true;

            // Plugin reference
            private ServerArmour plugin;
            public SAConfig(ServerArmour plugin) {
                this.plugin = plugin;
                /**
                 * Load all saved config values
                 * */
                GetConfig(ref ServerIp, "Server Info", "Your Server IP");
                GetConfig(ref ServerGPort, "Server Info", "Game Port");
                GetConfig(ref ServerQPort, "Server Info", "Query Port");
                GetConfig(ref ServerRPort, "Server Info", "RCON Port");

                GetConfig(ref IgnoreAdmins, "General", "Ignore Admins");
                GetConfig(ref Debug, "General", "Debug: Show additional debug console logs");


                GetConfig(ref ShowProtectedMsg, "Show Protected MSG");
                GetConfig(ref BetterChatDirtyPlayerTag, "Better Chat: Tag for dirty users");

                GetConfig(ref BroadcastPlayerBanReport, "Broadcast", "Player Reports");
                GetConfig(ref BroadcastPlayerBanReportVacDays, "Broadcast", "When VAC is younger than");
                GetConfig(ref BroadcastNewBans, "Broadcast", "New bans");
                GetConfig(ref BroadcastKicks, "Broadcast", "Kicks");
                GetConfig(ref RconBroadcast, "Broadcast", "RCON");

                GetConfig(ref ServerAdminShareDetails, "io.serverarmour.com", "Share details with other server owners");
                GetConfig(ref ServerApiKey, "io.serverarmour.com", "Server Key");
                GetConfig(ref ServerAdminName, "io.serverarmour.com", "Owner Real Name");
                GetConfig(ref ServerAdminEmail, "io.serverarmour.com", "Owner Email");
                GetConfig(ref OwnerSteamId, "io.serverarmour.com", "Owner Steam64 ID");
                GetConfig(ref SubmitArkanData, "io.serverarmour.com", "Submit Arkan Data");

                GetConfig(ref AutoKickOn, "Auto Kick", "Enabled");
                GetConfig(ref AutoKick_NetworkBan, "Auto Kick", "Bans on your network");
                GetConfig(ref AutoKickCeiling, "Auto Kick", "Max allowed previous bans");

                GetConfig(ref AutoKick_BadIp, "Auto Kick", "VPN", "Enabled");
                GetConfig(ref AutoKick_IgnoreNvidia, "Auto Kick", "VPN", "Ignore nVidia Cloud Gaming");

                GetConfig(ref AutoKick_KickTwitterGameBanned, "Auto Kick", "Users that have been banned on rusthackreport");

                GetConfig(ref AutoKick_KickPrivateProfile, "Auto Kick", "Steam", "Private Steam Profiles");
                GetConfig(ref AutoKick_KickHiddenLevel, "Auto Kick", "Steam", "When Steam Level Hidden");
                GetConfig(ref AutoKick_MinSteamProfileLevel, "Auto Kick", "Steam", "Min Allowed Steam Level (-1 disables)");
                GetConfig(ref AutoKick_KickWeirdSteam64, "Auto Kick", "Steam", "Profiles that do no conform to the Steam64 IDs (Highly recommended)");
                GetConfig(ref AutoVacBanCeiling, "Auto Kick", "Steam", "Max allowed VAC bans");
                GetConfig(ref AutoGameBanCeiling, "Auto Kick", "Steam", "Max allowed Game bans");
                GetConfig(ref DissallowVacBanDays, "Auto Kick", "Steam", "Min age of VAC ban allowed");
                GetConfig(ref AutoKickFamilyShare, "Auto Kick", "Steam", "Family share accounts");
                GetConfig(ref AutoKickFamilyShareIfDirty, "Auto Kick", "Steam", "Family share accounts that are dirty");

                GetConfig(ref DiscordWebhookURL, "Discord", "Webhook URL");
                GetConfig(ref DiscordBanWebhookURL, "Discord", "Bans Webhook URL");
                GetConfig(ref DiscordQuickConnect, "Discord", "Show Quick Connect On report");
                GetConfig(ref DiscordOnlySendDirtyReports, "Discord", "Send Only Dirty Player Reports");
                GetConfig(ref DiscordNotifyGameBan, "Discord", "Notify when a player has received a game ban");
                GetConfig(ref DiscordKickReport, "Discord", "Send Kick Report");
                GetConfig(ref DiscordBanReport, "Discord", "Send Ban Report");

                GetConfig(ref ClanBanPrefix, "Clan Ban", "Reason Prefix");
                GetConfig(ref ClanBanTeams, "Clan Ban", "Ban Native Team Members");

                plugin.SaveConfig();
            }

            private void GetConfig<T>(ref T variable, params string[] path) {
                if (path.Length == 0) return;

                if (plugin.Config.Get(path) == null) {
                    SetConfig(ref variable, path);
                    plugin.PrintWarning($"Added new field to config: {string.Join("/", path)}");
                }

                string serverAddress = plugin.covalence.Server.Address.ToString();
                if (path[path.Length - 1].Equals("Your Server IP") && string.IsNullOrEmpty(ServerIp) && !string.IsNullOrEmpty(serverAddress) && !serverAddress.Equals("0.0.0.0")) {
                    ServerIp = serverAddress;
                    SetConfig(ref variable, path);
                }

                variable = (T)Convert.ChangeType(plugin.Config.Get(path), typeof(T));
            }

            public void SetConfig<T>(ref T variable, params string[] path) => plugin.Config.Set(path.Concat(new object[] { variable }).ToArray());
        }

        protected override void LoadDefaultConfig() => PrintWarning("Generating new configuration file.");
        #endregion

        #region BOT Helpers
        Dictionary<ulong, string> _codes = new Dictionary<ulong, string>();

        /// <summary>
        /// Get's an authentication code for the steam64Id
        /// </summary>
        /// <param name="steamId"></param>
        /// <returns></returns>
        private string GenerateAuthCode(ulong steamId) {
            // Let's check if the player already has a code. 
            if (_codes.ContainsKey(steamId))
                return _codes[steamId];

            // Lets generate the first code, in hopes it's original.
            string code = _GenerateCode();

            while (CodeExistsAndValid(code)) { // Lets make sure it's original.
                code = _GenerateCode(); // If not, let's generate a new one. 
            }

            _codes.Add(steamId, code); // Original code was found, let's save it for the player. 
            return code;
        }

        /// <summary>
        /// Generates a random code.
        /// </summary>
        /// <returns></returns>
        private string _GenerateCode() {
            var code = "";
            var rnd = new System.Random();
            for (var i = 0; i < 4; i++) {
                code += rnd.Next(10).ToString();
            }
            return code;
        }

        /// <summary>
        /// Checks if the code was already assigned to a player. 
        /// </summary>
        /// <param name="code"></param>
        /// <returns></returns>
        bool CodeExistsAndValid(string code) => !code.IsNullOrEmpty() && _codes.Values.Contains(code);

        [Command("sa.auth.check")]
        void cmdCheckCode(IPlayer player, string command, string[] args) {
            if (!player.IsServer) return;
            ulong steamId = 0;

            if (ulong.TryParse(args[0], out steamId)) {
                if (_codes.ContainsKey(steamId)) {
                    // success
                    SendReplyWithIcon(player, _codes[steamId]);
                } else {
                    // no key found
                    SendReplyWithIcon(player, $"null");
                }
            } else {
                SendReplyWithIcon(player, $"null");
                // no steamid provided.
            }
        }

        /// <summary>
        /// Gives an auth code to the connected player, and saves it for later authing..
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        [Command("sa.auth")]
        void cmdAuthGen(IPlayer player, string command, string[] args) {
            if (player.IsServer) return;
            ulong steamId = ulong.Parse(player.Id);
            string code = "";
            if (_codes.ContainsKey(steamId)) {
                code = _codes[steamId];
            } else {
                code = GenerateAuthCode(steamId);
            }
            SendReplyWithIcon(player, $"Your auth code is <color=#d8b300>{code}</color>");
        }

        #endregion
    }
}
