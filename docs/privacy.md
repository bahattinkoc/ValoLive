# ValoLive Privacy Policy / Gizlilik Politikası

_Last updated / Son güncelleme: 2026-11-09_

ValoLive is a desktop streaming tool for Windows. It runs entirely on your own computer; there is no ValoLive server.

## What ValoLive accesses

When you connect a **YouTube** account (Google OAuth, scope `https://www.googleapis.com/auth/youtube`), ValoLive uses the YouTube Data API only to:

- find your own live broadcast,
- set that broadcast's title, tags and category (Gaming),
- post the chat-command replies and announcements you enable to your own live chat.

When you connect a **Kick** account, ValoLive uses the Kick API to set your stream title, category and tags, and to post chat replies to your own channel.

ValoLive reads your streams' public live chats to show them on stream.

## What ValoLive stores

- Access/refresh tokens and API client credentials are stored **only on your computer**, in `%LOCALAPPDATA%\ValoLive\settings.json`, encrypted with Windows DPAPI (readable only by your Windows user).
- No data is sent to the developer or to any third party. Data goes only to the platforms you connect (Google/YouTube, Kick) and to the Riot/Valorant services the app reads match data from.
- ValoLive does not sell, share or use your data for advertising.

## Removing access

Click **Çıkış / Log out** next to the account in ValoLive, and revoke access at <https://myaccount.google.com/permissions> (YouTube) or in your Kick settings. Deleting `%LOCALAPPDATA%\ValoLive` removes all stored data.

## Google API Services User Data Policy

ValoLive's use of information received from Google APIs adheres to the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including the Limited Use requirements.

## Contact

Open an issue at <https://github.com/bahattinkoc/ValoLive/issues>.

---

**Türkçe özet:** ValoLive yalnızca senin bilgisayarında çalışır. YouTube ve Kick'e bağlanınca yalnızca kendi yayınının başlığını, etiketlerini ve kategorisini ayarlar ve açtığın bot cevaplarını kendi chat'ine yazar. Oturum bilgileri yalnızca bilgisayarında, Windows şifrelemesiyle saklanır. Geliştiriciye ya da başka birine hiçbir veri gönderilmez.
