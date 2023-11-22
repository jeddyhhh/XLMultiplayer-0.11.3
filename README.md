# XLMultiplayer
Skater XL Multiplayer mod by silentbaws has reached end of life. With the release of official multiplayer there's no longer a space for the mod and will shutdown at the end of May 2021

All I've done is modify the existing mod to accept a custom master server IP and write a master server in node.js. 
If there was an issue in the original 0.11.2 mod, it will also be here.

Current issues:
- Custom maps not working. Something to do with XLMultiplayer mod not hashing maps correctly.
- XLSAdmin's client side plugin for XL Multiplayer fails to load, could be a .Net mismatch, not sure at all tbh. You can just delete the zip file it sends
and everything works fine but you lose the ability to ban via hardware ID's.

Official multiplayer went down for several days, so there is a space for this mod, modified to accept a custom IP for a new master server found here: https://github.com/jeddyhhh/New-Silents-Mod-Master-Server/tree/main

For SXL 1.1.0.4 only, porting this to 1.2.2.8 might be a challenge, I can see some pretty drastic differences between the 2 versions. 
