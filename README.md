Protect your gaming server against hackers, scripters, cheats and grievers!

The plugin allows you to auto kick known hackers and scripters, as well as cheaters, griefers, toxic playes, racist players etc, the list goes on and growing each day. 

You can also auto kick users that are on VPN, PROXY or a BAD IP (See more at the bottom)

This tool is a combination of wealth of information regarding players, from their vac ban counts, economy bans, game bans and server bans. It also gives you the family share information, if they are lending, and whom they are lending from, as well if the lender is either vac banned or community banned. 

Please note, this is a BETA plugin, so not all features are available yet. Autobanning is still being tested to make sure all is well, and should be available within a week.

## NOTE!! Version 0.2.0
You will need to delete you configuration file, as it will be recreated with default values. Keep your old config as a backup if you have custom settings to repopulate new config.

## Permissions
```
/sa.ban - requires permission serverarmour.ban
/sa.unban - requires permission serverarmour.unban
```

## Whitelist Permissions
```
serverarmour.whitelist.recentvac
serverarmour.whitelist.badip
serverarmour.whitelist.keyword
serverarmour.whitelist.vacceiling
serverarmour.whitelist.banceiling
serverarmour.whitelist.gamebanceiling
serverarmour.whitelist.hardware.ownsbloody
```

## Commands
```
<optional>

/sa.cp username <force:boolean> - This will show you the ServerArmour report for a specific user, when the force true is added, it will skip checking local cache and update it from the server.
/sa.unban "username/id" - unbans a user

/sa.ban "username/id" "reason" - This will ban a player for 1 hour, please keep reason english for now (this helps with sentiment analysis.)
/sa.ban "username/id" "reason" 1h - This will ban a player for 1 hour, please keep reason english for now (this helps with sentiment analysis.)
/sa.ban "username/id" "reason" 1d - This will ban a player for 1 day, please keep reason english for now (this helps with sentiment analysis.)
/sa.ban "username/id" "reason" 1m - This will ban a player for 1 month, please keep reason english for now (this helps with sentiment analysis.)
/sa.ban "username/id" "reason" 1y - This will ban a player for 1 year, please keep reason english for now (this helps with sentiment analysis.)
```

## Configuration

### Default Configuration

```json

{
  "API: Admin Email": "", // please fill in your main admins email. This is to add a better trust level to your server.
  "API: Admin Real Name": "", // please fill in your main admins real name. This is to add a better trust level to your server.
  "API: Owner Steam64 ID": "",
  "API: Server Key": "FREE", //leave as is
  "API: Share details with other server owners": true,
  "API: Submit Arkan Data": true, // submits to server, helps identifying players behaviours, will be usefull when website is online
  "Auto Kick": true, // turn auto kicking on or off. 
  "Auto Kick / Ban Group": "serverarmour.bans", // the group name that banned users should be added in
  // The below options are to ban based on sentiment analysis on previous bans within the reason. Currently ONLY english is supported. please note that autoban above should also be true for this to work. 
  "Auto Kick: Kick if user owns a bloody device (now and past)": true,
  "Auto Kick: Ban: Contains previous Aimbot ban": false,
  "Auto Kick: Ban: Contains previous Cheat ban": false,
  "Auto Kick: Ban: Contains previous ESP ban": false,
  "Auto Kick: Ban: Contains previous Hack ban": false,
  "Auto Kick: Ban: Contains previous Insult ban": false,
  "Auto Kick: Ban: Contains previous Ping ban": false,
  "Auto Kick: Ban: Contains previous Racism ban": false,
  "Auto Kick: Ban: Contains previous Script ban": false,
  "Auto Kick: Ban: Contains previous Toxic ban": false,
  "Auto Kick: Family share accounts": false, // Auto kick players that are lending (Family sharing) the game.
  "Auto Kick: Family share accounts that are dirty": false, // Auto kick players that are lending (Family sharing) the game, and the owner of the game is dirty.
  "Auto Kick: Max allowed Game bans": 2,
  "Auto Kick: Max allowed previous bans": 5,
  "Auto Kick: Max allowed VAC bans": 2, // Auto kick players with X amount of previous bans.
  "Auto Kick: Min age of VAC ban allowed": 90, //auto kicks users that have received a vac within these days
  "Auto Kick: VPN and Proxy": true, // WIll automatically kick a player if they are either using a proxy, vpn or is a bad IP,
  "Auto Kick: VPN and Proxy: Sensitivity": 1.0, //How sensitive it should be, max 1. Value of 1.0 will only kick known vpns and proxies, a value of 0.98 will kick all suspected vpns, proxies, and spamming ips
  "Better Chat: Tag for dirty users": "",
  "Broadcast: New bans": true,
  "Broadcast: Player Reports": true,
  "Broadcast: When VAC is younger than": 120,
  "Debug: Show additional debug console logs": false, // never turn on, unless asked to do so by the developer, otherwise your logs will contain tons of messages.
  "Discord: Send Player Reports": true,  // only send payers that have a dirty report to discord
  "Discord: Webhook URL": "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks",
  "Show Protected MSG": true
}
```
#### Bad IP: 
It refers any combination of crawlers / comment & email spammers / brute force attacks. IPs that are behaving "badly" in an automated manner. Networks that are infected with malware / trojans / botnet / etc are also considered "bad". It may be possible that the user is not aware that their systems are infected or they have received an IP by their ISP that was recently infected with malicious code. If you wish to skip this, see variations of implementation.

**Setting: **  
```json
"Auto Kick: VPN and Proxy": true
```
WIll automatically kick a player if they are either using a proxy, vpn or is a bad IP,
 
```json
"Auto Kick: VPN and Proxy: Sensitivity": 1.0
``` 
How sensitive it should be, max 1. Value of 1.0 will only kick known vpns and proxies, a value of 0.98 will kick all suspected vpns, proxies, and spamming ips

**More info regarding sensitivity from  https://getipintel.net**
If a value of 0.50 is returned, then it is as good as flipping a 2 sided fair coin, which implies it's not very accurate. From my personal experience, values > 0.95 should be looked at and values > 0.99 are most likely proxies. Anything below the value of 0.90 is considered as "low risk". Since a real value is returned, different levels of protection can be implemented. It is best for a system admin to test some sample datasets with this system and adjust implementation accordingly. 

I only recommend automated action on high values ( > 0.99 or even > 0.995 )

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