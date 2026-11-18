# GemakTV published files

This branch is written by the GemakTV manager app and publisher tool. Do not edit it by hand.

- `config.gtv` - the channel list and latest app release, encrypted (AES-256-GCM) with a key derived from the app signing key
- `apk/` - encrypted builds of the TV app

The TV app reads these with "Check For Updated Channel List" and "Check For Updates".