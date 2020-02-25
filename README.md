Protect your gaming server against hackers, scripters, cheats and grievers!

The plugin allows you to auto kick known hackers and scripters, as well as cheaters, griefers, toxic playes, racist players etc, the list goes on and growing each day. 

You can also auto kick users that are on VPN, PROXY or a BAD IP (See more at the bottom)

This tool is a combination of wealth of information regarding players, from their vac ban counts, economy bans, game bans and server bans. It also gives you the family share information, if they are lending, and whom they are lending from, as well if the lender is either vac banned or community banned. 

Please note, this is a BETA plugin, so not all features are available yet. Autobanning is still being tested to make sure all is well, and should be available within a week.
## Permissions
```
/sa.ban - requires permission serverarmour.ban
```

## Commands
```
<optional>

/sa.cp username <force:boolean> - This will show you the ServerArmour report for a specific user, when the force true is added, it will skip checking local cache and update it from the server.
/sa.ban "username/id" "reason" <days:int>- This will ban a player, please keep reason english for now (this helps with sentiment analysis.)
/sa.unban - unbans a user
```

## Configuration

### Default Configuration

```json
{
  "AutoBanCeiling": 1,// Auto ban players with X amount of previous bans.
  "AutoBanGroup": "serverarmour.bans", // the group name that banned users should be added in
  "AutoBanOn": true, // turn auto banning on or off. 
  "AutoBanReasonKeywords": [
    "aimbot",
    "esp"
  ], // auto ban users that have these keywords in previous ban reasons.
  "AutoBanFamilyShare": false, // Auto ban players that are lending (Family sharing) the game.
  "AutoBanFamilyShareIfDirty": false, // Auto ban players that are lending (Family sharing) the game, and the owner of the game is dirty.
  "Debug": false, // never turn on, unless asked to do so by the developer, otherwise your logs will contain tons of messages.
  "ServerAdminEmail": "", // please fill in your main admins email. This is to add a better trust level to your server.
  "ServerAdminName": "", // please fill in your main admins real name. This is to add a better trust level to your server.
  "ServerApiKey": "TEST", //leave as is
  "ServerName": "", // You can change this manually if you like, but will autopopulate each startup
  "ServerPort": "",  // You can change this manually if you like, but will autopopulate each startup
  "ServerVersion": "", // You can change this manually if you like, but will autopopulate each startup
  "WatchlistCeiling": 1, // Auto add players with X amount of previous bans to a watchlist.
  "WatchlistGroup": "serverarmour.watchlist", // the group name that watched users should be added in

  // The below options are to ban based on sentiment analysis on previous bans within the reason. Currently ONLY english is supported. please note that autoban above should also be true for this to work. 
  "AutoBan_Reason_Keyword_Aimbot": false,
  "AutoBan_Reason_Keyword_Hack": false,
  "AutoBan_Reason_Keyword_EspHack": false,
  "AutoBan_Reason_Keyword_Script": false,
  "AutoBan_Reason_Keyword_Cheat": false,
  "AutoBan_Reason_Keyword_Toxic": false,
  "AutoBan_Reason_Keyword_Insult": false,
  "AutoBan_Reason_Keyword_Ping": false,
  "AutoBan_Reason_Keyword_Racism": false,

  "AutoKick_BadIp": false // WIll automatically kick a player if they are either using a proxy, vpn or is a bad IP;
}
```
#### Bad IP: 
It refers any combination of crawlers / comment & email spammers / brute force attacks. IPs that are behaving "badly" in an automated manner. Networks that are infected with malware / trojans / botnet / etc are also considered "bad". It may be possible that the user is not aware that their systems are infected or they have received an IP by their ISP that was recently infected with malicious code. If you wish to skip this, see variations of implementation.
#### Service used: 
https://getipintel.net

## API Hooks

### Methods


```csharp
int API_GetServerBanCount(string steamid) // Get the count of servers this use has been banned on
bool API_GetIsVacBanned(string steamid) // Indicates whether or not the player has VAC bans on record.
bool API_GetIsCommunityBanned(string steamid) // Indicates whether or not the player is banned from Steam Community
int API_GetVacBanCount(string steamid) // Number of VAC bans on record.
int API_GetGameBanCount(string steamid) // Number of bans in games, this includes CS:GO Overwatch bans.
string API_GetEconomyBanStatus(string steamid) // The player's ban status in the economy. If the player has no bans on record the string will be "none", if the player is on probation it will say "probation", etc.
bool API_GetIsPlayerDirty(string steamid) // Indicates if the player has any bans at all, includes server, game and vac bans
bool API_GetIsPlayerDirty(string steamid) // Indicates if the game is a family shared game, true indicates the player doesnt own it but lending it. 
string API_GetFamilyShareLenderSteamId(string steamid) // Gets the steamid of the person lending the game. Returns "0" if there isn't a lender and it's not family share.
bool API_GetIsFamilyShareLenderDirty(string steamid) // Checks if the current users family share account is dirty
int API_GetDaysSinceLastVacBan(string steamid) // Get amount of days since last vac ban. This will retun 0 if there is no vac ban
```


## Example

```csharp
[PluginReference]
Plugin ServerArmour;

private void OnUserConnected(IPlayer player) {
{
    Puts(ServerArmour.Call<bool>("API_GetIsPlayerDirty", player.Id));
}
```
The above is a universal example using the universal OnUserConnected hook for all Oxide supported games.

## More Info
The plugin makes web calls to Server Armours api, which is a collection and aggregated database of multiple databases containing bans of steamid's. 

Information sent to the api is as follows:

#### local server ban information
* player steamid - only reliable way to track all information related to a player. 
* player username
* player ip
* reason - used to display reasons for a ban, and also for Sentiment analysis. (when users need to ban specific people (scripters, hackers, esp, aimbot, etc)) 
* * date and time

 #### server information
* server name - to identify your server name
* server port - server port, not currently used by our services, but will be used in the future for server owners to manage their server from a web based management console. 
* server admin name - By default the admin needs to set this up, this is used to identify how trustworty a ban that is being submitted is. This will also be used for banned users to contact the relevant server admins, this information is NEVER made public or sold. This is only used to make the service fair for all involved and so that there is a dispute process. When not provided, your bans will have the lowest of trust scores. 
* server admin email - By default the admin needs to set this up, this is used to identify how trustworty a ban that is being submitted is. This will also be used for banned users to contact the relevant server admins, this information is NEVER made public or sold. This is only used to make the service fair for all involved and so that there is a dispute process. When not provided, your bans will have the lowest of trust scores. 
* steam game id - the steam game id, to identify what game the server is actually hosting.
* game name - same as above, but just in readable format.

If more information is needed regarding any of the above, or any concerns, please open a thread so that I can provide more information.