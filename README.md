# YouTube AutoPost — Windows application

A WinUI 3 application that syncs a local video folder to the currently connected YouTube channel.

## First-time setup

### 1. Create a Google Cloud project

➡️ Open [Google Cloud Console](https://console.cloud.google.com/).

- Sign in with your Google account.
- Open the project selector.
- Click **New project**.
- Give it a name, for example **YouTubeAutoPost**.
- Click **Create**.

### 2. Enable YouTube Data API v3

➡️ Open [YouTube Data API v3 — Google Cloud Library](https://console.cloud.google.com/apis/library?project=hip-limiter-515413-s7).

- Search for **YouTube Data API v3**.
- Open the result.
- Click **Enable**.

### 3. Configure the OAuth consent screen

➡️ Open [Audience — Google Auth Platform](https://console.cloud.google.com/auth/audience?organizationId=0&project=hip-limiter-515413-s7).

- Set the application name.
- Enter a contact email address.
- Choose the appropriate audience type (usually **External** for a personal Google account).
- Add your Google account under **Test users**.
- Save your changes.

### 4. Create an OAuth client

➡️ Open [OAuth Clients — Google Auth Platform](https://console.cloud.google.com/auth/clients?organizationId=0&project=hip-limiter-515413-s7).

- Click **Create client**.
- Application type: **Desktop app**.
- Name: **YouTubeAutoPost**.
- Click **Create**.

### 5. Download the JSON file

- Download the OAuth client JSON file.
- Keep it somewhere safe.
- This is the file the application expects, named `client_secret.json`.

### 6. Connect YouTubeAutoPost

1. Launch the application. If no OAuth file has been installed yet, the **Add OAuth JSON** button will appear.
2. Click it and select the JSON file you downloaded from Google.
3. Click **Connect** and complete the authorization process in your browser.
4. Choose a folder, destination, and privacy setting.
5. Click **Sync**.

The original OAuth file is copied into the application's private data folder. Authentication tokens are stored separately and encrypted using DPAPI.

## Changing the connected Google account

**The easiest way to switch accounts is to reinstall the application.**

When you uninstall YouTube AutoPost, the uninstaller offers to delete the data associated with the application. Choose that option to remove the previous account's saved OAuth credentials and tokens. Then install or launch YouTube AutoPost again and import the OAuth JSON if prompted. Connect using the Google account associated with the YouTube channel you want to use.

If you prefer not to remove the application data through the uninstaller, you can change the OAuth credentials manually:

1. Completely close YouTube AutoPost (choose **Quit** from the notification-area icon menu).
2. Open File Explorer and paste this path into the address bar:

   ```
   %LOCALAPPDATA%\\YouTubeAutoPost
   ```

3. Replace `client_secret.json` with the new JSON file downloaded from Google. Keep the exact filename.
4. Delete the `oauth-token` folder in the same location. This forces the application to request authorization again using the new credentials.
5. Launch YouTube AutoPost and click **Connect**. Sign in with the new Google account.

The replacement JSON must belong to an OAuth client whose application type is **Desktop app**.

## Important: channel and playlist

YouTube Data API associates each upload with the channel linked to the active OAuth authorization. The standard API does not allow an arbitrary `channelId` to be selected in `videos.insert`. To switch channels, connect with the Google account or Brand Account identity that has access to the desired channel. **My channel** publishes directly to that channel; **A playlist** publishes to the same channel and then adds the video to the selected playlist.

## Build

**Requirements:** Windows 10 version 2004 (build 19041) or later, 64-bit Windows, PowerShell, and the .NET 8 SDK.
