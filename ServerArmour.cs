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
using Time = Oxide.Core.Libraries.Time;

namespace Oxide.Plugins
{
    [Info("Server Armour", "Pho3niX90", "0.4.992")]
    [Description("Protect your server! Auto ban known hackers, scripters and griefer accounts, and notify server owners of threats.")]
    class ServerArmour : CovalencePlugin
    {

        #region Variables
        Dictionary<string, ISAPlayer> _playerData = new Dictionary<string, ISAPlayer>();
        private Time _time = GetLibrary<Time>();
        private double cacheLifetime = 30; // minutes
        private SAConfig config;
        string specifier = "G";
        bool debug = false;
        CultureInfo culture = CultureInfo.CreateSpecificCulture("en-US");
        //StringComparison defaultCompare = StringComparison.InvariantCultureIgnoreCase;
        const string DATE_FORMAT = "yyyy/MM/dd HH:mm";
        ulong ServerArmourId = 76561199060671869L;
        #endregion

        #region Permissions
        const string PermissionToBan = "serverarmour.ban";
        const string PermissionToUnBan = "serverarmour.unban";
        const string PermissionWhitelistRecentVacKick = "serverarmour.whitelist.recentvac";
        const string PermissionWhitelistBadIPKick = "serverarmour.whitelist.badip";
        const string PermissionWhitelistKeywordKick = "serverarmour.whitelist.keyword";
        const string PermissionWhitelistVacCeilingKick = "serverarmour.whitelist.vacceiling";
        const string PermissionWhitelistServerCeilingKick = "serverarmour.whitelist.banceiling";
        const string PermissionWhitelistGameBanCeilingKick = "serverarmour.whitelist.gamebanceiling";
        const string PermissionWhitelistHardwareOwnsBloody = "serverarmour.whitelist.hardware.ownsbloody";
        const string PermissionWhitelistSteamProfile = "serverarmour.whitelist.steamprofile";

        const string GroupBloody = "serverarmour.hardware.ownsbloody";
        #endregion

        #region Plugins
        [PluginReference] Plugin DiscordApi, DiscordMessages, BetterChat;

        void DiscordSend(string steamId, string name, EmbedFieldList report, int color = 39423, bool isBan = false) {
            string webHook;
            if (isBan) {
                if (config.DiscordBanWebhookURL.Length == 0 && !config.DiscordBanWebhookURL.Equals("https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks")) { Puts("Discord webhook not setup."); return; }
                webHook = config.DiscordBanWebhookURL;
            } else {
                if (config.DiscordWebhookURL.Length == 0 && !config.DiscordWebhookURL.Equals("https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks")) { Puts("Discord webhook not setup."); return; }
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
                Puts("No discord API plugin loaded, will not publish to hook!");
            }
        }

        #endregion

        #region Hooks
        void Init() {
            config = new SAConfig(this);
            LoadData();

            CheckOnlineUsers();
            CheckLocalBans();

            Puts("Server Armour is being initialized.");
            string serverAddress = covalence.Server.Address.ToString();
            if (string.IsNullOrEmpty(config.ServerIp) && !string.IsNullOrEmpty(serverAddress) && !serverAddress.Equals("0.0.0.0")) {
                config.ServerIp = serverAddress;
            }
            SaveConfig();
            Puts($"Server IP is {config.ServerIp}");
            AddGroup(GroupBloody);

            RegPerm(PermissionToBan);
            RegPerm(PermissionToUnBan);

            RegPerm(PermissionWhitelistBadIPKick);
            RegPerm(PermissionWhitelistKeywordKick);
            RegPerm(PermissionWhitelistRecentVacKick);
            RegPerm(PermissionWhitelistServerCeilingKick);
            RegPerm(PermissionWhitelistVacCeilingKick);
            RegPerm(PermissionWhitelistGameBanCeilingKick);
            RegPerm(PermissionWhitelistSteamProfile);
            RegisterTag();
            Puts("Server Armour has initialized.");
        }

        void Unload() {
            Puts("Server Armour unloading, will now save all data.");
            SaveData();
            _playerData.Clear();
            Puts("Server Armour finished unloaded.");
        }

        void OnUserConnected(IPlayer player) {

            //lets check the userid first.
            if (config.AutoKick_KickWeirdSteam64 && !player.Id.StartsWith("7656119")) {
                KickPlayer(player.Id, GetMsg("Strange Steam64ID"), "C");
                return;
            }

            GetPlayerBans(player, true, "C");
            if (config.ShowProtectedMsg) SendReplyWithIcon(player, GetMsg("Protected MSG"));
        }

        void OnUserDisconnected(IPlayer player) {
            GetPlayerBans(player, true, "D");
            SaveThenPurge(player.Id);
        }

        void OnPluginLoaded(Plugin plugin) {
            if (plugin.Title == "BetterChat") RegisterTag();
        }

        void OnUserUnbanned(string name, string id, string ipAddress) {
            SaUnban(id);
        }

        void OnUserBanned(string name, string id, string ipAddress, string reason) {

            //this is to make sure that if an app like battlemetrics for example, bans a player, we catch it.
            timer.Once(1f, () => {
                //lets make sure first it wasn't us. 
                if (!IsPlayerCached(id) && (IsPlayerCached(id) && !ContainsMyBan(id))) {
                    Puts($"Player wasn't banned via Server Armour, now adding to DB with a default lengh ban of 100yrs {name} ({id}) at {ipAddress} was banned: {reason}");
                    IPlayer bPlayer = players.FindPlayerById(id);
                    AddBan(bPlayer, new ISABan {
                        serverName = server.Name,
                        serverIp = config.ServerIp,
                        reason = reason,
                        date = DateTime.Now.ToString(DATE_FORMAT),
                        banUntil = DateTime.Now.AddYears(100).ToString(DATE_FORMAT)
                    });

                }
            });
        }


        void OnPlayerReported(BasePlayer reporter, string targetName, string targetId, string subject, string message, string type) {
            string messageClean = Uri.EscapeDataString(message);
            string subjectClean = Uri.EscapeDataString(subject);
            webrequest.Enqueue("https://io.serverarmour.com/api/plugin/addf7" + ServerGetString("?"), $"reporter={reporter.UserIDString}&target={targetId}&subject={subjectClean}&message={messageClean}", (code, response) => {
                if (code != 200 || response == null) {
                    Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                    return;
                }
            }, this, RequestMethod.POST);
        }

        void OnUserKicked(IPlayer player, string reason) {
            Puts($"Player {player.Name} ({player.Id}) was kicked, {reason}");
            if (!reason.Equals(GetMsg("Kick Bloody")) && (reason.ToLower().Contains("bloody") || reason.ToLower().Contains("a4") || reason.ToLower().Contains("blacklisted"))) {
                AssignGroup(player.Id, GroupBloody);
                webrequest.Enqueue("https://io.serverarmour.com/api/plugin/addBloodyKicks" + ServerGetString("?"), $"steamid={player.Id}&reason={reason}", (code, response) => {
                    if (code != 200 || response == null) {
                        Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                        return;
                    }
                }, this, RequestMethod.POST);
            } else if (!reason.Equals(GetMsg("Kick Bloody")) && (reason.ToLower().Contains("gameban") || reason.ToLower().Contains("PublisherIssuedBan") || reason.ToLower().Contains("anticheat"))) {
                webrequest.Enqueue("https://io.serverarmour.com/api/plugin/addGameBan" + ServerGetString("?"), $"steamid={player.Id}&reason={reason}", (code, response) => {
                    if (code != 200 || response == null) {
                        Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                        return;
                    }
                }, this, RequestMethod.POST);

                if (config.DiscordNotifyGameBan) {
                    DiscordSend(player.Id, player.Name, new EmbedFieldList() {
                        name = "Player Game Banned",
                        value = reason,
                        inline = true
                    }, 13459797);
                }
            }
        }
        #endregion

        #region API_Hooks

        #endregion

        #region WebRequests

        void GetPlayerBans(IPlayer player, bool reCache = false, string type = "C") {
            if (type == "C" && IsCacheValid(player?.Id)) KickIfBanned(GetPlayerCache(player?.Id));
            _webCheckPlayer(player.Name, player.Id, player.Address, player.IsConnected, type);
        }

        void _webCheckPlayer(string name, string id, string address, Boolean connected, string type) {
            string playerName = Uri.EscapeDataString(name);
            string url = $"https://io.serverarmour.com/api/plugin/checkUser?steamid={id}&ip={address}&t={type}" + ServerGetString();
            //Puts(url);
            string resp = "";
            try {
                webrequest.Enqueue(url, null, (code, response) => {
                    LogDebug(url);
                    resp = response;
                    if (code != 200 || response == null) {
                        Puts(url);
                        Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                        return;
                    }
                    ISAPlayer isaPlayer = JsonConvert.DeserializeObject<ISAPlayer>(response);
                    isaPlayer.cacheTimestamp = _time.GetUnixTimestamp();
                    isaPlayer.lastConnected = _time.GetUnixTimestamp();

                    // add cache for player
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
                        Puts($"PlayerName `{playerName}`\nUrl: `{url}`\nIsaPlayer? {isaPlayer != null}\nIsLender {isaPlayer?.lendersteamid}");
                        Puts(response);
                        Puts(ane.Message);
                    }

                    //script vars
                    ISASteamData pSteam = isaPlayer.steamData;
                    ISASteamData lSteam = isaPlayer.lenderSteamData;
                    string pSteamId = isaPlayer.steamid;
                    string lSteamId = isaPlayer.lendersteamid;
                    //

                    // now lets check for a recent vac
                    bool pRecentVac = (pSteam.NumberOfVACBans > 0) && pSteam.DaysSinceLastBan < config.DissallowVacBanDays; //check main player
                    bool lRecentVac = (lSteam.NumberOfVACBans > 0) && lSteam.DaysSinceLastBan < config.DissallowVacBanDays; //check the lender player

                    if (config.AutoKickOn && !HasPerm(pSteamId, PermissionWhitelistRecentVacKick) && (pRecentVac || lRecentVac)) {
                        int vacLast = pRecentVac ? pSteam.DaysSinceLastBan : lSteam.DaysSinceLastBan;
                        int until = config.DissallowVacBanDays - vacLast;

                        Interface.CallHook("OnSARecentVacKick", pSteam, vacLast, until);

                        string msg = GetMsg(pRecentVac ? "Reason: VAC Ban Too Fresh" : "Reason: VAC Ban Too Fresh - Lender", new Dictionary<string, string> { ["daysago"] = vacLast.ToString(), ["daysto"] = until.ToString() });
                        KickPlayer(isaPlayer?.steamid, msg, "C");
                    }

                    // lets check if this user is using VPN
                    if (config.AutoKickOn && !HasPerm(pSteamId, PermissionWhitelistBadIPKick) && config.AutoKick_BadIp && IsBadIp(isaPlayer)) {
                        Interface.CallHook("OnSAVPNKick", pSteamId, isaPlayer.ipRating);
                        if (IsVpn(isaPlayer)) {
                            KickPlayer(pSteamId, GetMsg("Reason: Proxy IP"), "C");
                        } else {
                            KickPlayer(pSteamId, GetMsg("Reason: Bad IP"), "C");
                        }
                    }

                    // does the user contain a keyword ban
                    if (!HasPerm(pSteamId, PermissionWhitelistKeywordKick) && HasKeywordBan(isaPlayer)) {
                        Interface.CallHook("OnSAKeywordKick", pSteamId);
                        KickPlayer(isaPlayer?.steamid, GetMsg("Keyword Kick"), "C");
                    }

                    // Kick players with too many VACs
                    if (!HasPerm(pSteamId, PermissionWhitelistVacCeilingKick) && HasReachedVacCeiling(isaPlayer)) {
                        Interface.CallHook("OnSATooManyVacKick", pSteamId, isaPlayer.steamData.NumberOfVACBans);
                        KickPlayer(isaPlayer?.steamid, GetMsg("VAC Ceiling Kick"), "C");
                    }

                    // Kick players with too many game bans
                    if (!HasPerm(pSteamId, PermissionWhitelistGameBanCeilingKick) && HasReachedGameBanCeiling(isaPlayer)) {
                        Interface.CallHook("OnSATooManyGameBansKick", pSteamId, isaPlayer.steamData.NumberOfGameBans);
                        KickPlayer(isaPlayer?.steamid, GetMsg("Too Many Previous Game Bans"), "C");
                    }

                    // Kick bloody/a4 owners
                    if (!HasPerm(pSteamId, PermissionWhitelistHardwareOwnsBloody) && (OwnsBloody(isaPlayer) || HasGroup(pSteamId, GroupBloody))) {
                        Interface.CallHook("OnSABloodyKick", pSteamId);
                        KickPlayer(isaPlayer?.steamid, GetMsg("Kick Bloody"), "C");
                    }

                    // Kick players with too many bans
                    if (!HasPerm(pSteamId, PermissionWhitelistServerCeilingKick) && HasReachedServerCeiling(isaPlayer)) {
                        Interface.CallHook("OnSATooManyBans", pSteamId, config.AutoKickCeiling, isaPlayer.serverBanCount + isaPlayer.lenderServerBanCount);
                        KickPlayer(isaPlayer?.steamid, GetMsg("Too Many Previous Bans"), "C");
                    }

                    // Kick players with private profiles
                    if (!HasPerm(pSteamId, PermissionWhitelistSteamProfile) && config.AutoKick_KickPrivateProfile && isaPlayer.communityvisibilitystate == 1) {
                        Interface.CallHook("OnSAProfilePrivate", pSteamId, isaPlayer.communityvisibilitystate);
                        KickPlayer(isaPlayer?.steamid, GetMsg("Profile Private"), "C");
                    }

                    // Kick players with hidden steam level
                    if (!HasPerm(pSteamId, PermissionWhitelistSteamProfile) && isaPlayer.steamLevel == -1 && config.AutoKick_KickHiddenLevel) {
                        Interface.CallHook("OnSASteamLevelHidden", pSteamId);
                        KickPlayer(isaPlayer?.steamid, GetMsg("Steam Level Hidden"), "C");
                    }

                    // Kick players low steam profile level
                    Puts($"Player {isaPlayer.steamid} is at steam level {isaPlayer.steamLevel}");
                    if (!HasPerm(pSteamId, PermissionWhitelistSteamProfile) && isaPlayer.steamLevel < config.AutoKick_MinSteamProfileLevel) {
                        Interface.CallHook("OnSAProfileLevelLow", pSteamId, config.AutoKick_MinSteamProfileLevel, isaPlayer.steamLevel);
                        KickPlayer(isaPlayer?.steamid, GetMsg("Profile Low Level", new Dictionary<string, string> { ["level"] = config.AutoKick_MinSteamProfileLevel.ToString() }), "C");
                    }

                    GetPlayerReport(isaPlayer, connected);
                }, this, RequestMethod.GET);
            } catch (Exception ice) {
                Puts("An ArgumentNullException occured. Please notify the developer along with the below information: ");
                Puts($"PlayerName `{playerName}`\nUrl: `{url}`");
                Puts(resp);
                Puts(ice.Message);
                return;
            }
        }

        void KickIfBanned(ISAPlayer isaPlayer) {
            if (isaPlayer == null) return;

            ISABan ban = IsBanned(isaPlayer?.steamid);
            if (ban != null) KickPlayer(isaPlayer?.steamid, ban.reason, "U");
            if (isaPlayer.lenderBanned) KickPlayer(isaPlayer?.steamid, GetMsg("Lender Banned"), "U");
        }


        void AddBan(IPlayer player, ISABan thisBan) {
            if (thisBan == null) return;
            DateTime now = DateTime.Now;
            string reason = Uri.EscapeDataString(thisBan.reason);
            string url = $"https://io.serverarmour.com/api/plugin/addBan?steamid={player.Id}&ip={player?.Address}&reason={reason}&dateTime={thisBan.date}&dateUntil={thisBan.banUntil}" + ServerGetString();
            webrequest.Enqueue(url, null, (code, response) => {
                LogDebug(url);
                if (code != 200 || response == null) { Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response })); return; }
                // ISABan thisBan = new ISABan { serverName = server.Name, date = dateTime, reason = banreason, serverIp = thisServerIp, banUntil = dateBanUntil };
                if (IsPlayerCached(player.Id)) {
                    LogDebug($"{player.Id} has ban cached, now updating.");
                    AddPlayerData(player.Id, thisBan);
                } else {
                    LogDebug($"{player.Id} had no ban data cached, now creating.");
                    ISAPlayer newPlayer = new ISAPlayer(player);
                    newPlayer.serverBanData.Add(thisBan);
                    AddPlayerCached(player, newPlayer);
                }
                //SaveData();
            }, this, RequestMethod.GET);
        }

        void AddBan(string playerId, ISABan thisBan) {
            if (thisBan == null) {
                Puts($"This ban is null");
                return;
            }
            DateTime now = DateTime.Now;
            string reason = Uri.EscapeDataString(thisBan.reason);
            string url = $"https://io.serverarmour.com/api/plugin/addBan?steamid={playerId}&ip=0.0.0.0&reason={reason}&dateTime={thisBan.date}&dateUntil={thisBan.banUntil}" + ServerGetString();
            string resp = "";
            try {
                webrequest.Enqueue(url, null, (code, response) => {
                    resp = response;
                    LogDebug(url);
                    if (code != 200 || response == null) { Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response })); return; }
                    // ISABan thisBan = new ISABan { serverName = server.Name, date = dateTime, reason = banreason, serverIp = thisServerIp, banUntil = dateBanUntil };
                    if (IsPlayerCached(playerId)) {
                        LogDebug($"{playerId} has ban cached, now updating.");
                        AddPlayerData(playerId, thisBan);
                    } else {
                        LogDebug($"{playerId} had no ban data cached, now creating.");
                        ISAPlayer newPlayer = new ISAPlayer().CreatePlayer(playerId);
                        newPlayer.serverBanData.Add(thisBan);
                        AddPlayerCached(newPlayer.steamid, newPlayer);
                    }
                    //SaveData();
                }, this, RequestMethod.GET);
            } catch (Exception ice) {
                Puts("An ArgumentNullException occured. Please notify the developer along with the below information: ");
                Puts($"Player `{playerId}`\nUrl: `{url}`");
                Puts(resp);
                Puts(ice.Message);
                return;
            }
        }

        string GetBanReason(ISAPlayer isaPlayer) {
            return isaPlayer?.serverBanData.First(x => x.serverIp.Equals(config.ServerIp)).reason;
        }
        #endregion

        #region Commands
        [Command("sa.clb", "getreport")]
        void SCmdCheckLocalBans(IPlayer player, string command, string[] args) {
            CheckLocalBans();
        }

        [Command("unban", "playerunban", "sa.unban"), Permission(PermissionToUnBan)]
        void SCmdUnban(IPlayer player, string command, string[] args) {
            if (args == null || (args.Length != 1)) {
                SendReplyWithIcon(player, GetMsg("UnBan Syntax"));
                return;
            }
            SaUnban(args[0], player);
        }

        void SaUnban(string playerId, IPlayer player = null) {
            LogDebug("Will now unban");

            IPlayer iPlayer = players.FindPlayer(playerId);
            if (iPlayer == null) { GetMsg("Player Not Found", new Dictionary<string, string> { ["player"] = playerId }); return; }
            if (iPlayer != null && iPlayer.IsBanned) iPlayer.Unban();
            RemoveBans(iPlayer.Id);
            Puts($"Player {iPlayer.Name} ({iPlayer.Id}) at {iPlayer.Address} was unbanned");

            RCon.Broadcast(RCon.LogType.Chat, new Chat.ChatEntry {
                Message = "Unbanned",
                UserId = iPlayer.Id,
                Username = iPlayer.Name,
                Time = Facepunch.Math.Epoch.Current
            });

            RCon.Broadcast(RCon.LogType.Chat, new Chat.ChatEntry {
                Message = $"Unbanned player {iPlayer.Name} {iPlayer.Id}",
                UserId = player.Id,
                Username = player.Name,
                Time = Facepunch.Math.Epoch.Current
            });

            webrequest.Enqueue("https://io.serverarmour.com/api/server/bans_remove" + ServerGetString("?"), $"steamid={iPlayer.Id}", (code, response) => {
                if (code != 200 || response == null) {
                    Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                    return;
                }
            }, this, RequestMethod.POST);
        }

        [Command("ban", "playerban", "sa.ban"), Permission(PermissionToBan)]
        void SCmdBan(IPlayer player, string command, string[] args) {
            int argsLength = args == null ? 0 : args.Length;
            /***
             * Length 2: player, reason
             * Length 3: player, reason, time
             * Length 4: playerSteamId, reason, time, ignoreSearch
             ***/
            if (args == null || (argsLength < 2 || argsLength > 4)) {
                SendReplyWithIcon(player, GetMsg("Ban Syntax"));
                return;
            }

            string banPlayer = args[0];
            string banReason = args[1];
            ulong banSteamId = 0;
            bool ignoreSearch = false;

            DateTime now = DateTime.Now;
            string dateTime = now.ToString(DATE_FORMAT);
            /***
             * If time specified, default to 100 years
             ***/
            string lengthOfBan = argsLength >= 3 ? args[2] : "100y";
            string dateBanUntil = _BanUntil(lengthOfBan).ToString(DATE_FORMAT);

            if (argsLength == 4) {
                try {
                    ignoreSearch = bool.Parse(args[3]);
                    banSteamId = ulong.Parse(args[0]);
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
                    date = dateTime,
                    banUntil = dateBanUntil
                })) {
                string msg;
                string banLengthText = lengthOfBan.Equals("100y") ? GetMsg("Permanent") : BanFor(lengthOfBan);

                msg = GetMsg("Player Now Banned Perma", new Dictionary<string, string> { ["player"] = playerName, ["reason"] = args[1], ["length"] = banLengthText });
                string msgClean = GetMsg("Player Now Banned Clean", new Dictionary<string, string> { ["player"] = playerName, ["reason"] = args[1], ["length"] = banLengthText });


                RCon.Broadcast(RCon.LogType.Chat, new Chat.ChatEntry {
                    Message = msgClean,
                    UserId = playerId,
                    Username = playerName,
                    Time = Facepunch.Math.Epoch.Current
                });

                RCon.Broadcast(RCon.LogType.Chat, new Chat.ChatEntry {
                    Message = $"Banned player {playerName} {playerId}, reason: {banReason}",
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
                    }, 13459797);
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

        DateTime _BanUntil(string banLength) {
            int digit = int.Parse(new string(banLength.Where(char.IsDigit).ToArray()));
            string del = new string(banLength.Where(char.IsLetter).ToArray());
            if (digit <= 0) {
                digit = 100;
            }

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
                    dateBanUntil = now.AddMonths(digit);
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
            if (_playerData.ContainsKey(id) && _playerData[id].serverBanData.Count > 0) {
                _playerData[id].serverBanData.RemoveAll(x => x.serverIp == config.ServerIp || x.adminSteamId.Equals(config.OwnerSteamId));
                SavePlayerData(id);
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
            IEnumerable<IPlayer> allPlayers = players.Connected;
            int allPlayersCount = allPlayers.Count();
            int allPlayersCounter = 0;
            float waitTime = 1f;
            if (allPlayersCount > 0)
                timer.Repeat(waitTime, allPlayersCount, () => {
                    LogDebug("Will now inspect all online users, time etimation: " + (allPlayersCount * waitTime) + " seconds");
                    LogDebug($"Inpecting online user {allPlayersCounter + 1} of {allPlayersCount} for infractions");
                    try {
                        IPlayer player = allPlayers.ElementAt(allPlayersCounter);
                        if (player != null) GetPlayerBans(player, true, "U");
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
                                    date = DateTime.Now.ToString(DATE_FORMAT),
                                    banUntil = expireDate.ToString(DATE_FORMAT)
                                });
                            } else {
                                AddBan(usr.steamid.ToString(), new ISABan {
                                    serverName = server.Name,
                                    serverIp = config.ServerIp,
                                    reason = usr.notes,
                                    date = DateTime.Now.ToString(DATE_FORMAT),
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
            return GetPlayerCache(steamid)?.lendersteamid;
        }

        bool IsPlayerDirty(ISAPlayer isaPlayer) {
            return isaPlayer != null && (isaPlayer?.serverBanCount > 0 || isaPlayer?.steamData?.CommunityBanned > 0 || isaPlayer?.steamData?.NumberOfGameBans > 0 || isaPlayer?.steamData?.VACBanned > 0);
        }

        bool IsPlayerDirty(string steamid) {
            ISAPlayer isaPlayer = GetPlayerCache(steamid);
            return isaPlayer != null && IsPlayerCached(steamid) && (isaPlayer?.serverBanCount > 0 || isaPlayer?.steamData?.CommunityBanned > 0 || isaPlayer?.steamData?.NumberOfGameBans > 0 || isaPlayer?.steamData?.VACBanned > 0);
        }

        bool IsPlayerCached(string steamid) { return _playerData != null && _playerData.Count > 0 && _playerData.ContainsKey(steamid); }
        void AddPlayerCached(ISAPlayer isaplayer) => _playerData.Add(isaplayer.steamid, isaplayer);
        void AddPlayerCached(IPlayer iplayer, ISAPlayer isaplayer) => _playerData.Add(iplayer.Id, isaplayer);
        void AddPlayerCached(string id, ISAPlayer isaplayer) => _playerData.Add(id, new ISAPlayer().CreatePlayer(id));
        ISAPlayer GetPlayerCache(string steamid) {
            LoadPlayerData(steamid);
            return IsPlayerCached(steamid) ? _playerData[steamid] : null;
        }
        List<ISABan> GetPlayerBanData(string steamid) => _playerData[steamid].serverBanData;
        int GetPlayerBanDataCount(string steamid) => _playerData[steamid].serverBanData.Count;
        void UpdatePlayerData(ISAPlayer isaplayer) => _playerData[isaplayer.steamid] = isaplayer;
        void AddPlayerData(string id, ISABan isaban) => _playerData[id].serverBanData.Add(isaban);
        IPlayer FindIPlayer(string identifier) => players.FindPlayer(identifier);

        bool IsCacheValid(string id) {
            LoadPlayerData(id);
            if (!_playerData.ContainsKey(id)) return false;
            return minutesToGo(_playerData[id].cacheTimestamp) < cacheLifetime;
        }

        int minutesToGo(uint to) {
            uint currentTimestamp = _time.GetUnixTimestamp();
            return (int)Math.Round((currentTimestamp - to) / 60.0);
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

                RCon.Broadcast(RCon.LogType.Chat, new Chat.ChatEntry {
                    Message = GetMsg("User Dirty DISCORD MSG", data),
                    UserId = isaPlayer.steamid,
                    Username = isaPlayer.username,
                    Time = Facepunch.Math.Epoch.Current
                });

            }
        }

        private void LogDebug(string txt) {
            if (config.Debug || debug) Puts(txt);
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

            IEnumerable<ServerUsers.User> bannedUsers = ServerUsers.GetAll(ServerUsers.UserGroup.Banned);
            int BannedUsersCount = bannedUsers.Count();
            int BannedUsersCounter = 0;
            while (BannedUsersCounter < BannedUsersCount) {
                LoadPlayerData(bannedUsers.ElementAt(BannedUsersCounter)?.steamid.ToString());
                BannedUsersCounter++;
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
            if (!string.IsNullOrEmpty(id) && !_playerData.ContainsKey(id)) {
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
            if (!string.IsNullOrEmpty(id) && _playerData.ContainsKey(id)) {
                Interface.Oxide.DataFileSystem.WriteObject($"ServerArmour/{id}", _playerData[id], true);
            }
        }

        string ServerGetString() {
            return ServerGetString("&");
        }

        string ServerGetString(string start) {
            string sname = Uri.EscapeDataString(server.Name);
            string aname = Uri.EscapeDataString(config.ServerAdminName);
            string aemail = Uri.EscapeDataString(config.ServerAdminEmail);
            return start + $"sip={config.ServerIp}&sn={sname}&sp={server.Port}&an={aname}&ae={aemail}&auid={config.OwnerSteamId}&gameId={covalence.ClientAppId}&gameName={covalence.Game}&v={this.Version}";
        }
        #endregion

        #region Kicking 

        void KickPlayer(string steamid, string reason, string type) {
            IPlayer player = players.FindPlayerById(steamid);
            if (player == null) return;

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

                if (type.Equals("D") && config.BroadcastNewBans) {
                    BroadcastWithIcon(GetMsg("Player Kicked", new Dictionary<string, string> { ["player"] = player.Name, ["reason"] = reason }));
                }
            }
        }

        bool IsVpn(ISAPlayer isaPlayer) {
            return config.AutoKick_BadIp && isaPlayer.ipRating == 1;
        }

        bool IsBadIp(ISAPlayer isaPlayer) {
            return (config.AutoKick_BadIp && isaPlayer.ipRating >= config.AutoKick_BadIp_Sensitivity);
        }

        bool ContainsMyBan(string steamid) {
            return IsBanned(steamid) != null;
        }

        ISABan IsBanned(string steamid) {
            if (steamid == null || steamid.Equals("0")) return null;
            if (!IsPlayerCached(steamid)) return null;
            try {
                ISAPlayer isaPlayer = GetPlayerCache(steamid);

                if (!config.OwnerSteamId.IsNullOrEmpty() && config.OwnerSteamId.StartsWith("7656")) {
                    return isaPlayer?.serverBanData.Count() > 0 ? isaPlayer?.serverBanData?.First(x => (x.serverIp.Equals(config.ServerIp) || x.adminSteamId.Equals(config.OwnerSteamId))) : null;
                }

                return isaPlayer?.serverBanData.Count() > 0 ? isaPlayer?.serverBanData?.First(x => x.serverIp.Equals(config.ServerIp) && minutesToGo(x.GetUnixBanUntill()) > 0) : null;
            } catch (Exception ioe) {
                if (_playerData.ContainsKey(steamid)) {
                    _playerData.Remove(steamid);
                }
                Puts("My Bans");
                Puts(ioe.StackTrace.ToString());
                return null;
            }
        }

        bool IsLenderDirty(string steamid) {
            string lendersteamid = GetFamilyShareLenderSteamId(steamid);
            return lendersteamid != "0" ? IsPlayerDirty(lendersteamid) : false;
        }


        bool HasReachedVacCeiling(ISAPlayer isaPlayer) {
            return config.AutoKickOn && config.AutoVacBanCeiling < (isaPlayer.steamData.NumberOfVACBans + isaPlayer.lenderSteamData.NumberOfVACBans);
        }

        bool HasReachedGameBanCeiling(ISAPlayer isaPlayer) {
            return config.AutoKickOn && config.AutoGameBanCeiling < (isaPlayer.steamData.NumberOfGameBans + isaPlayer.lenderSteamData.NumberOfGameBans);
        }

        bool OwnsBloody(ISAPlayer isaPlayer) {
            return config.AutoKickOn && config.AutoKick_Hardware_Bloody && isaPlayer.bloodyScriptsCount > 0;
        }

        bool HasReachedServerCeiling(ISAPlayer isaPlayer) {
            return config.AutoKickOn && config.AutoKickCeiling < (isaPlayer.serverBanCount + isaPlayer.lenderServerBanCount);
        }

        bool IsFamilyShare(string steamid) {
            if (steamid.Length != 17 || string.IsNullOrEmpty(steamid)) return false;
            ISAPlayer player = GetPlayerCache(steamid);
            return player != null && player.lendersteamid != null && !player.lendersteamid.Equals("0");
        }

        bool IsProfilePrivate(string steamid) {
            ISAPlayer player = GetPlayerCache(steamid);
            return player.communityvisibilitystate == 1;
        }

        int GetProfileLevel(string steamid) {
            ISAPlayer player = GetPlayerCache(steamid);
            return player.steamLevel;
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
        private bool API_GetIsProfilePrivate(string steamid) => IsProfilePrivate(steamid);
        private int API_GetProfileLevel(string steamid) => GetProfileLevel(steamid);
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
                ["Reason: Bad IP"] = "Bad IP Detected, either due to a VPN/Proxy",
                ["Reason: Proxy IP"] = "VPN & Proxy's not allowed.",
                ["Player Not Found"] = "Player wasn't found",
                ["Multiple Players Found"] = "Multiple players found with that name ({players}), please try something more unique like a steamid",
                ["Ban Syntax"] = "sa.ban <playerNameOrID> \"<reason>\" length (example: 1h for 1 hour, 1m for 1 month etc)",
                ["UnBan Syntax"] = "sa.unban <playerNameOrID>",
                ["No Response From API"] = "Couldn't get an answer from ServerArmour.com! Error: {code} {response}",
                ["Player Not Banned"] = "Player not banned",
                ["Broadcast Player Banned"] = "{tag} {username} wasn't allowed to connect\nReason: {reason}",
                ["Reason: VAC Ban Too Fresh"] = "VAC ban received {daysago} days ago, wait another {daysto} days",
                ["Reason: VAC Ban Too Fresh - Lender"] = "VAC ban received {daysago} days ago on lender account, wait another {daysto} days",
                ["Lender Banned"] = "The lender account contained a ban",
                ["Keyword Kick"] = "Due to your past behaviour on other servers, you aren't allowed in.",
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

        string FixColors(string msg) {
            return msg.Replace("[/#]", "</color>").Replace("[", "<color=").Replace("]", ">");
        }

        void AddGroup(string group) {
            if (!permission.GroupExists(group)) permission.CreateGroup(group, "Bloody Mouse Owners", 0);
        }

        void AssignGroup(string id, string group) => permission.AddUserGroup(id, group);
        bool HasGroup(string id, string group) => permission.UserHasGroup(id, group);
        void RegPerm(string perm) {
            if (!permission.PermissionExists(perm, this)) permission.RegisterPermission(perm, this);
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
            return DateTime.ParseExact(stringDate, DATE_FORMAT, null);
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
            public string steamid { get; set; }
            public int communityvisibilitystate { get; set; }
            public int steamLevel { get; set; }
            public int bloodyScriptsCount { get; set; }
            public string lendersteamid { get; set; }
            public ISASteamData lenderSteamData { get; set; }
            public string username { get; set; }
            public ISASteamData steamData { get; set; }
            public bool lenderBanned { get; set; }
            public int serverBanCount { get; set; }
            public uint cacheTimestamp { get; set; }
            public uint lastConnected { get; set; }
            public int lenderServerBanCount { get; set; }
            public double ipRating { get; set; }
            public List<ISABan> serverBanData { get; set; }

            public ISAPlayer() {
            }
            public ISAPlayer(ulong steamId) {
                steamid = steamId.ToString();
                username = "";
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
                username = bPlayer.Name;
                cacheTimestamp = new Time().GetUnixTimestamp();
                lendersteamid = "0";
                lastConnected = new Time().GetUnixTimestamp();
                lenderSteamData = new ISASteamData();
                steamData = new ISASteamData();
                serverBanData = new List<ISABan>();
                return this;
            }
            public ISAPlayer CreatePlayer(string id) {
                steamid = id;
                bloodyScriptsCount = 0;
                username = "";
                cacheTimestamp = new Time().GetUnixTimestamp();
                lendersteamid = "0";
                lastConnected = new Time().GetUnixTimestamp();
                lenderSteamData = new ISASteamData();
                steamData = new ISASteamData();
                serverBanData = new List<ISABan>();
                return this;
            }
        }

        public class ISABan
        {
            public string banId;
            public string adminSteamId;
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

            public uint GetUnixBanUntill() {
                return ConvertToTimestamp(banUntil);
            }
        }

        public class ISASteamData
        {
            public int CommunityBanned { get; set; }
            public int VACBanned { get; set; }
            public int NumberOfVACBans { get; set; }
            public int DaysSinceLastBan { get; set; }
            public int NumberOfGameBans { get; set; }
            public string EconomyBan { get; set; }
            public string BansLastCheckUTC { get; set; }
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

                webrequest.Enqueue("https://io.serverarmour.com/api/plugin/addArkan" + ServerGetString("?"), $"uid={player.UserIDString}&vp={violationProbability}&sc={shotsCnt}&ammo={ammoShortName}&weapon={weaponShortName}&attach={attachments}&snrs={suspiciousNoRecoilShots}", (code, response) => {
                    if (code != 200 || response == null) {
                        Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                        return;
                    }
                }, this, RequestMethod.POST);
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

                Puts("https://io.serverarmour.com/api/plugin/addArkan_Aim" + ServerGetString("?") + $"uid={player.UserIDString}&attach={attachments}&ammo={ammoShortName}&weapon={weaponShortName}&dmg={damage}&bp={bodypart}&distance={hitInfoProjectileDistance}&hits={hitsData}");

                webrequest.Enqueue("https://io.serverarmour.com/api/plugin/addArkan_Aim" + ServerGetString("?"), $"uid={player.UserIDString}&attach={attachments}&ammo={ammoShortName}&weapon={weaponShortName}&dmg={damage}&bp={bodypart}&distance={hitInfoProjectileDistance}&hits={hitsData}", (code, response) => {
                    if (code != 200 || response == null) {
                        Puts(GetMsg("No Response From API", new Dictionary<string, string> { ["code"] = code.ToString(), ["response"] = response }));
                        return;
                    }
                }, this, RequestMethod.POST);

            }
        }

        #endregion

        #region Configuration
        private class SAConfig
        {
            // Config default vars
            public string ServerIp = "";
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
            public bool ServerAdminShareDetails = true;
            public string ServerAdminName = string.Empty;
            public string ServerAdminEmail = string.Empty;
            public string ServerApiKey = "FREE";

            public bool AutoKick_Hardware_Bloody = true;
            public bool AutoKick_KickHiddenLevel = false;
            public int AutoKick_MinSteamProfileLevel = -1;
            public bool AutoKick_KickPrivateProfile = false;
            public bool AutoKick_KickWeirdSteam64 = true;

            public bool AutoKick_Reason_Keyword_Aimbot = false;
            public bool AutoKick_Reason_Keyword_Hack = false;
            public bool AutoKick_Reason_Keyword_EspHack = false;
            public bool AutoKick_Reason_Keyword_Script = false;
            public bool AutoKick_Reason_Keyword_Cheat = false;
            public bool AutoKick_Reason_Keyword_Toxic = false;
            public bool AutoKick_Reason_Keyword_Insult = false;
            public bool AutoKick_Reason_Keyword_Ping = false;
            public bool AutoKick_Reason_Keyword_Racism = false;
            public bool AutoKick_BadIp = true;
            public double AutoKick_BadIp_Sensitivity = 1.0;

            public string DiscordWebhookURL = "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks";
            public string DiscordBanWebhookURL = "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks";
            public bool DiscordQuickConnect = true;
            public bool DiscordOnlySendDirtyReports = true;
            public bool DiscordKickReport = true;
            public bool DiscordBanReport = true;
            public bool DiscordNotifyGameBan = true;
            public bool SubmitArkanData = true;

            public string OwnerSteamId = "";

            // Plugin reference
            private ServerArmour plugin;
            public SAConfig(ServerArmour plugin) {
                this.plugin = plugin;
                /**
                 * Load all saved config values
                 * */
                GetConfig(ref ServerIp, "Your Server IP");
                GetConfig(ref Debug, "Debug: Show additional debug console logs");

                GetConfig(ref ShowProtectedMsg, "Show Protected MSG");
                GetConfig(ref BetterChatDirtyPlayerTag, "Better Chat: Tag for dirty users");
                GetConfig(ref BroadcastPlayerBanReport, "Broadcast: Player Reports");
                GetConfig(ref BroadcastPlayerBanReportVacDays, "Broadcast: When VAC is younger than");
                GetConfig(ref BroadcastNewBans, "Broadcast: New bans");

                GetConfig(ref ServerAdminShareDetails, "API: Share details with other server owners");
                GetConfig(ref ServerAdminName, "API: Admin Real Name");
                GetConfig(ref ServerAdminEmail, "API: Admin Email");
                GetConfig(ref ServerApiKey, "API: Server Key");
                GetConfig(ref SubmitArkanData, "API: Submit Arkan Data");
                GetConfig(ref OwnerSteamId, "API: Owner Steam64 ID");

                GetConfig(ref AutoKickOn, "Auto Kick");
                GetConfig(ref AutoKickCeiling, "Auto Kick: Max allowed previous bans");
                GetConfig(ref AutoVacBanCeiling, "Auto Kick: Max allowed VAC bans");
                GetConfig(ref AutoGameBanCeiling, "Auto Kick: Max allowed Game bans");
                GetConfig(ref DissallowVacBanDays, "Auto Kick: Min age of VAC ban allowed");
                GetConfig(ref AutoKickFamilyShare, "Auto Kick: Family share accounts");
                GetConfig(ref AutoKickFamilyShareIfDirty, "Auto Kick: Family share accounts that are dirty");
                GetConfig(ref AutoKick_Hardware_Bloody, "Auto Kick: Kick if user owns a bloody device (now and past)");

                GetConfig(ref AutoKick_Reason_Keyword_Aimbot, "Auto Kick: Ban: Contains previous Aimbot ban");
                GetConfig(ref AutoKick_Reason_Keyword_Hack, "Auto Kick: Ban: Contains previous Hack ban");
                GetConfig(ref AutoKick_Reason_Keyword_EspHack, "Auto Kick: Ban: Contains previous ESP ban");
                GetConfig(ref AutoKick_Reason_Keyword_Script, "Auto Kick: Ban: Contains previous Script ban");
                GetConfig(ref AutoKick_Reason_Keyword_Cheat, "Auto Kick: Ban: Contains previous Cheat ban");
                GetConfig(ref AutoKick_Reason_Keyword_Toxic, "Auto Kick: Ban: Contains previous Toxic ban");
                GetConfig(ref AutoKick_Reason_Keyword_Insult, "Auto Kick: Ban: Contains previous Insult ban");
                GetConfig(ref AutoKick_Reason_Keyword_Ping, "Auto Kick: Ban: Contains previous Ping ban");
                GetConfig(ref AutoKick_Reason_Keyword_Racism, "Auto Kick: Ban: Contains previous Racism ban");

                GetConfig(ref AutoKick_BadIp, "Auto Kick: VPN and Proxy");
                GetConfig(ref AutoKick_BadIp_Sensitivity, "Auto Kick: VPN and Proxy: Sensitivity");

                GetConfig(ref AutoKick_KickPrivateProfile, "Auto Kick: Private Steam Profiles");
                GetConfig(ref AutoKick_KickHiddenLevel, "Auto Kick: When Steam Level Hidden");
                GetConfig(ref AutoKick_MinSteamProfileLevel, "Auto Kick: Min Allowed Steam Level (-1 disables)");
                GetConfig(ref AutoKick_KickWeirdSteam64, "Auto Kick: Profiles that do no conform to the Steam64 IDs (Highly recommended)");

                GetConfig(ref DiscordWebhookURL, "Discord: Webhook URL");
                GetConfig(ref DiscordBanWebhookURL, "Discord: Bans Webhook URL");
                GetConfig(ref DiscordQuickConnect, "Discord: Show Quick Connect On report");
                GetConfig(ref DiscordOnlySendDirtyReports, "Discord: Send Only Dirty Player Reports");
                GetConfig(ref DiscordNotifyGameBan, "Discord: Notify when a player has received a game ban");
                GetConfig(ref DiscordKickReport, "Discord: Send Kick Report");
                GetConfig(ref DiscordBanReport, "Discord: Send Ban Report");

                plugin.SaveConfig();
            }

            private void GetConfig<T>(ref T variable, params string[] path) {
                if (path.Length == 0) return;

                if (plugin.Config.Get(path) == null) {
                    SetConfig(ref variable, path);
                    plugin.PrintWarning($"Added new field to config: {string.Join("/", path)}");
                }
                string serverAddress = plugin.covalence.Server.Address.ToString();
                if (path.Equals("Your Server IP") && string.IsNullOrEmpty(ServerIp) && !string.IsNullOrEmpty(serverAddress) && !serverAddress.Equals("0.0.0.0")) {
                    ServerIp = serverAddress;
                    SetConfig(ref variable, path);
                }

                variable = (T)Convert.ChangeType(plugin.Config.Get(path), typeof(T));
            }

            private void SetConfig<T>(ref T variable, params string[] path) => plugin.Config.Set(path.Concat(new object[] { variable }).ToArray());
        }

        protected override void LoadDefaultConfig() => PrintWarning("Generating new configuration file.");
        #endregion
    }
}
