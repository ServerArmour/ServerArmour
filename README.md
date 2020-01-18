Protect your gaming server against hackers, scripters, cheats and grievers!

Please note, this is a BETA plugin, so not all features are available yet. Autobanning is still being tested to make sure all is well, and should be available within a week.

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
  "Debug": false, // never turn on, unless asked to do so by the developer, otherwise your logs will contain tons of messages.
  "ServerAdminEmail": "", // please fill in your main admins email. This is to add a better trust level to your server.
  "ServerAdminName": "", // please fill in your main admins real name. This is to add a better trust level to your server.
  "ServerApiKey": "TEST", //leave as is
  "ServerName": "", // You can change this manually if you like, but will autopopulate each startup
  "ServerPort": "",  // You can change this manually if you like, but will autopopulate each startup
  "ServerVersion": "", // You can change this manually if you like, but will autopopulate each startup
  "WatchlistCeiling": 1, // Auto add players with X amount of previous bans to a watchlist.
  "WatchlistGroup": "serverarmour.watchlist" // the group name that watched users should be added in
}
```

## API Hooks

### Methods


```
int API_GetServerBanCount(string steamid) // Get the count of servers this use has been banned on
bool API_GetIsVacBanned(string steamid) // Indicates whether or not the player has VAC bans on record.
bool API_GetIsCommunityBanned(string steamid) // Indicates whether or not the player is banned from Steam Community
int API_GetVacBanCount(string steamid) // Number of VAC bans on record.
int API_GetGameBanCount(string steamid) // Number of bans in games, this includes CS:GO Overwatch bans.
string API_GetEconomyBanStatus(string steamid) // The player's ban status in the economy. If the player has no bans on record the string will be "none", if the player is on probation it will say "probation", etc.
bool API_GetIsPlayerDirty(string steamid) // Indicates if the player has any bans at all, includes server, game and vac bans
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
* local server ban information
* * player steamid - only reliable way to track all information related to a player. 
* * player username
* * player ip
* * reason - used to display reasons for a ban, and also for Sentiment analysis. (when users need to ban specific people (scripters, hackers, esp, aimbot, etc)) 
* * date and time

* server information
* * server name - to identify your server name
* * server port - server port, not currently used by our services, but will be used in the future for server owners to manage their server from a web based management console. 
* * server admin name - By default the admin needs to set this up, this is used to identify how trustworty a ban that is being submitted is. This will also be used for banned users to contact the relevant server admins, this information is NEVER made public or sold. This is only used to make the service fair for all involved and so that there is a dispute process. When not provided, your bans will have the lowest of trust scores. 
* * server admin email - By default the admin needs to set this up, this is used to identify how trustworty a ban that is being submitted is. This will also be used for banned users to contact the relevant server admins, this information is NEVER made public or sold. This is only used to make the service fair for all involved and so that there is a dispute process. When not provided, your bans will have the lowest of trust scores. 
* * steam game id - the steam game id, to identify what game the server is actually hosting.
* * game name - same as above, but just in readable format.

If more information is needed regarding any of the above, or any concerns, please open a thread so that I can provide more information.