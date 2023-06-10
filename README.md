Protect your gaming server against hackers, scripters, cheats and grievers!

The plugin allows you to auto kick known hackers and scripters, as well as cheaters, griefers, toxic playes, racist players etc, the list goes on and growing each day. 

**Note: ** All auto kick features are configurable by server, so you can make use of the banDB or just use the features that are made available. 

You can also auto kick users that are on VPN, PROXY or a BAD IP (See more at the bottom)

This tool is a combination of wealth of information regarding players, from their vac ban counts, economy bans, game bans and server bans. It also gives you the family share information, if they are lending, and whom they are lending from, as well if the lender is either vac banned or community banned. 

## API Key
* You can get your api key from [https://io.serverarmour.com/my-servers?action=servers](https://io.serverarmour.com/my-servers?action=servers)

## Disclaimer:
* If you are an abusive & biased admin, your server ip will be blacklisted from using this service. 

## Discord: nd54sKX
You can add the Server Armour bot to your discord by following this link:

## Steam API Key
A new config field has been added since version 1, "Steam API Key", whilst this is not necassary to work, it does provide you with a small docker container for your servers to check and reference information. Since the influx of servers we are not able to keep up with the information from our steam keys alone, and some information is cached. 

This also gives you basic premium on the website. 

[Add the ServerArmour discord bot to your discord.](https://discord.com/api/oauth2/authorize?client_id=781921686202220575&permissions=281373767&scope=bot)
## Permissions
```
/sa.ban - requires permission serverarmour.ban
/sa.unban - requires permission serverarmour.unban
/clanban - requires permission serverarmour.ban
```

## Admin Permissions
```
serverarmour.website.admin
serverarmour.ban
serverarmour.unban
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
serverarmour.whitelist.steamprofile
serverarmour.whitelist.twitterban
serverarmour.whitelist.familyshare
```

## Commands
```
<optional>

/sa.cp username <force:boolean> - This will show you the ServerArmour report for a specific user, when the force true is added, it will skip checking local cache and update it from the server.
/sa.unban "username/id" - unbans a user

/sa.ban "username/id" "reason" - This will ban a player for 1 hour, please keep reason english for now (this helps with sentiment analysis.)
/clanban "username/id" "reason" - This will ban a player for 1 hour, please keep reason english for now (this helps with sentiment analysis.)
/sa.ban "username/id" "reason" 1h - This will ban a player for 1 hour, please keep reason english for now (this helps with sentiment analysis.)
/sa.ban "username/id" "reason" 1d - This will ban a player for 1 day, please keep reason english for now (this helps with sentiment analysis.)
/sa.ban "username/id" "reason" 1m - This will ban a player for 1 month, please keep reason english for now (this helps with sentiment analysis.)
/sa.ban "username/id" "reason" 1y - This will ban a player for 1 year, please keep reason english for now (this helps with sentiment analysis.)
```

## Configuration

### Default Configuration
```json
{
  "Auto Kick": {
    "Bans on your network": true, // should SA auto kick bans create on any of your other servers you are admin on?
    "Enabled": true, // Is auto kick enabled?
    "Max allowed previous bans": 3, // max allowed bans on other servers, for a player
    "Steam": {
      "Min age of VAC ban allowed": 90, // Example: a player with a 89day vac should be kicked, 90  wont.
      "When Steam Level Hidden": false, // Kicks a player that has a hidden steam level, this includes private accounts.
      "Family share accounts": false, // kick family share accounts?
      "Family share accounts that are dirty": false, // kick family share accounts that are considered dirty?
      "Max allowed Game bans": 2, 
      "Max allowed VAC bans": 1,
      "Min Allowed Steam Level (-1 disables)": -1, //
      "Private Steam Profiles": false,
      "Profiles that do no conform to the Steam64 IDs (Highly recommended)": true
    },
    "Users that have been banned on rusthackreport": true,
    "VPN": {
      "Enabled": true, // should vpn or proxy players be kicked?
      "Ignore nVidia Cloud Gaming": true, // should players on the nvidia network be ignored?
    }
  },
  "Better Chat: Tag for dirty users": "", //will prefix player names with this tag that are dirty
  "Broadcast": { // this will broadcast in chat by default
    "Kicks": false, // when a player gets kicked?
    "New bans": true, // when a player gets banned?
    "RCON": false, // should it all the above be broadcasted via RCON as well (usefull for battlemetrics player history)
    "Player Reports": true, //should their player report be broadcasted on connect
    "When VAC is younger than": 120 // goes together with the below
  },
  "Clan Ban": {
    "Ban Native Team Members": true, // this is the normal team members in vanilla rust, by default will ban members in a Clan in Clans or ClansReborn
    "Reason Prefix": "Assoc Ban -> {playerId}: {reason}" // will use this reason prefifx format.
  },
  "Discord": {
    "Webhook URL": "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks", // webhook for connect reports, and kicks
    "Bans Webhook URL": "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks", // webhook for bans, else it will default to above
    "Notify when a player has received a game ban": true,
    "Send Ban Report": true,
    "Send Kick Report": true,
    "Send Only Dirty Player Reports": true, //if only reports should be sent to discord when a player is dirty, if false, it will send a report for every player that connects
    "Show Quick Connect On report": true //this will embed a clickable link in the report to the server connect.
  },
  "General": {
    "Debug: Show additional debug console logs": false, // always false, unless you want to debug where an issue occurs for the developer.
    "Ignore Admins": true // this will ignore admins completely.
  },
  "io.serverarmour.com": {
    "Owner Email": "", // owner email, not required, but usefull for important communication (not spam)
    "Owner Real Name": "", // your name, the owner
    "Owner Steam64 ID": "", // the owners steam64id
    "Server Key": "", // Get this from the website
    "Steam API Key": "" // This is your steam api key, not needed, but if provided, will give you more accurate and up to date information.
    "Share details with other server owners": true, // For future use, so that other server admins can send you emails for evidence etc, or discuss a ban.
    "Submit Arkan Data": true // if arkan data can be submitted to the cloud server, for analysis. 
  },
  "Server Info": {
    "Game Port": "", // your normal port, that users connect to
    "Query Port": "", // if you havent changed this, the default is the same as gameport.
    "RCON Port": "", // not used now, for future management from io.serverarmour.com
    "Your Server IP": "" // your server IP ONLY, without : ports
  },
  "Show Protected MSG": true // shows that your server is protected by serverarmour, to a player that connects
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

#### Service used: 
https://proxycheck.io/

## API Hooks

```csharp
	void OnSARecentVacKick(string steamId, int unixLastVax, int unixRemainingDays) {
	
	}
	void OnSAVPNKick(string steamId, double ipRating) {
	
	}
	void OnSAKeywordKick(string steamId) {
	
	}
	void OnSATooManyVacKick(string steamId, int numberOfVACBans) {
	
	}
	void OnSATooManyGameBansKick(string steamId, int numberOfGameBans) {
	
	}
	void OnSABloodyKick(string steamId) {
	
	}
	void OnSATooManyBans(string steamId) {
	
	}
	void OnSAProfilePrivate(string steamId, int communityvisibilitystate) {
	
	}
	void OnSAProfileLevelLow(string steamId, int minlevelallowed, int userlevel) {
	
	}
```
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
bool API_GetIsFamilyShareLenderDirty(string steamid) // Checks if the current users family share account is dirty.
int API_GetDaysSinceLastVacBan(string steamid) // Get amount of days since last vac ban. This will retun 0 if there is no vac ban.
bool API_GetIsProfilePrivate(string steamid) // Check if the players profile is private.
int API_GetProfileLevel(string steamid) // Gets the players steam level.
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

Please consider supporting the project.
